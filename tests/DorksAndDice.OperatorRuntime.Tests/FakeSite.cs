using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DorksAndDice.OperatorRuntime.Tests;

internal sealed class FakeSite : IAsyncDisposable
{
    private const string AuthCookieName = "dd-test-auth";
    private const string AuthCookieValue = "normal-site-session";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte> _bootstraps = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _mutationStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mutationRelease =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _mutationCount;

    private FakeSite(bool rejectOperator)
    {
        RejectOperator = rejectOperator;
        OperatorToken = "ddop_v1_test_fixture_operator_secret";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        _app = builder.Build();

        _app.Use(async (context, next) =>
        {
            Requests.Enqueue(new RequestRecord(
                context.Request.Path,
                context.Request.Headers.Authorization.ToString(),
                context.Request.Headers.Cookie.ToString()));
            await next();
        });

        _app.MapGet("/operator/v1/me", (HttpContext context) =>
        {
            if (!OperatorAuthorized(context))
            {
                return Results.Unauthorized();
            }

            return Results.Json(new
            {
                userId = TestUserId,
                displayName = "Operator Runtime Test",
                accountKind = "ServicePrincipal",
                globalRoles = new[] { "RulesLawyer" }
            });
        });

        _app.MapPost("/operator/v1/browser-bootstrap", (HttpContext context) =>
        {
            if (!OperatorAuthorized(context))
            {
                return Results.Unauthorized();
            }

            var token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
            _bootstraps[token] = 0;
            LastBootstrapUrl = $"/operator/bootstrap?token={token}";
            return Results.Json(new
            {
                bootstrapId = Guid.NewGuid(),
                bootstrapUrl = LastBootstrapUrl,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
            });
        });

        _app.MapGet("/operator/bootstrap", (HttpContext context) =>
        {
            var token = context.Request.Query["token"].ToString();
            if (string.IsNullOrWhiteSpace(token) || !_bootstraps.TryRemove(token, out _))
            {
                return Results.Unauthorized();
            }

            context.Response.Cookies.Append(
                AuthCookieName,
                AuthCookieValue,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/"
                });
            return Results.Redirect("/");
        });

        _app.MapGet("/account", (HttpContext context) =>
            BrowserAuthorized(context)
                ? Results.Content("<html><head><title>Account</title></head><body><h1>Account</h1></body></html>", "text/html")
                : Results.Redirect("/account/login?returnUrl=%2Faccount"));

        _app.MapGet("/account/login", () =>
            Results.Content("<html><head><title>Login</title></head><body><h1>Login</h1></body></html>", "text/html"));

        _app.MapGet("/", (HttpContext context) =>
            BrowserAuthorized(context)
                ? Results.Content(HomePage, "text/html")
                : Results.Content("<html><body><h1>Public home</h1></body></html>", "text/html"));

        _app.MapGet("/tools/rules-core", (HttpContext context) =>
            BrowserAuthorized(context)
                ? Results.Content("<html><head><title>Rules Core</title></head><body><h1>Rules Core</h1></body></html>", "text/html")
                : Results.Redirect("/account/login"));

        _app.MapGet("/submitted", (HttpContext context) =>
        {
            if (!BrowserAuthorized(context))
            {
                return Results.Redirect("/account/login");
            }

            var search = context.Request.Query["search"].ToString();
            return Results.Content(
                $"<html><head><title>Submitted</title></head><body><h1>Submitted</h1><p>{System.Net.WebUtility.HtmlEncode(search)}</p></body></html>",
                "text/html");
        });

        _app.MapGet("/diagnostics", (HttpContext context) =>
            BrowserAuthorized(context)
                ? Results.Content(
                    "<html><head><title>Diagnostics</title></head><body><h1>Diagnostics</h1><script>console.log('operator-runtime-console-marker'); fetch('/missing?token=network-secret');</script></body></html>",
                    "text/html")
                : Results.Redirect("/account/login"));

        _app.MapGet("/missing", () => Results.NotFound());

        _app.MapGet("/mutating", (HttpContext context) =>
            BrowserAuthorized(context)
                ? Results.Content(
                    "<html><head><title>Mutating</title></head><body><h1>Mutating</h1><form method='post' action='/mutate-slow'><button type='submit' aria-label='Mutate slowly'>Mutate slowly</button></form></body></html>",
                    "text/html")
                : Results.Redirect("/account/login"));

        _app.MapPost("/mutate-slow", async (HttpContext context) =>
        {
            if (!BrowserAuthorized(context))
            {
                return Results.Redirect("/account/login");
            }

            Interlocked.Increment(ref _mutationCount);
            _mutationStarted.TrySetResult();
            await Task.WhenAny(_mutationRelease.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            return Results.Content("<html><body><h1>Mutation complete</h1></body></html>", "text/html");
        });
    }

    public Guid TestUserId { get; } = Guid.Parse("7f1f78e8-6f61-42db-988a-2333a0e5eafd");
    public string OperatorToken { get; }
    public bool RejectOperator { get; }
    public Uri SiteUri { get; private set; } = null!;
    public string? LastBootstrapUrl { get; private set; }
    public ConcurrentQueue<RequestRecord> Requests { get; } = new();
    public int MutationCount => Volatile.Read(ref _mutationCount);
    public Task MutationStarted => _mutationStarted.Task;

    public static async Task<FakeSite> StartAsync(bool rejectOperator = false)
    {
        var site = new FakeSite(rejectOperator);
        await site._app.StartAsync();
        var server = site._app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Fake Site did not expose a listening address.");
        site.SiteUri = new Uri(addresses.Single().TrimEnd('/') + "/");
        return site;
    }

    public void ReleaseMutation() => _mutationRelease.TrySetResult();

    private bool OperatorAuthorized(HttpContext context) =>
        !RejectOperator
        && string.Equals(
            context.Request.Headers.Authorization.ToString(),
            $"Bearer {OperatorToken}",
            StringComparison.Ordinal);

    private static bool BrowserAuthorized(HttpContext context) =>
        context.Request.Cookies.TryGetValue(AuthCookieName, out var value)
        && string.Equals(value, AuthCookieValue, StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        ReleaseMutation();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private const string HomePage = """
        <html>
          <head><title>Runtime Test Home</title></head>
          <body>
            <h1>Runtime Test Home</h1>
            <a href="/tools/rules-core">Rules Core</a>
            <form method="get" action="/submitted">
              <label for="search">Search</label>
              <input id="search" name="search" aria-label="Search" />
              <button type="submit" aria-label="Submit">Submit</button>
            </form>
            <button type="button" aria-label="Increment" onclick="document.getElementById('count').textContent='1'">Increment</button>
            <span id="count">0</span>
          </body>
        </html>
        """;
}

internal sealed record RequestRecord(string Path, string Authorization, string Cookie);
