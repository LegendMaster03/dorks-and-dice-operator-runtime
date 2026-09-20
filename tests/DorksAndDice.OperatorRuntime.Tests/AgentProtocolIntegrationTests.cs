using System.Net;
using System.Text.Json;
using DorksAndDice.OperatorRuntime.AgentProtocol;
using DorksAndDice.OperatorRuntime.Browser;
using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Security;
using DorksAndDice.OperatorRuntime.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DorksAndDice.OperatorRuntime.Tests;

public sealed class AgentProtocolIntegrationTests
{
    private const string RuntimeApiToken = "runtime-api-test-secret";

    [Fact]
    public async Task McpCallerCanDriveAuthenticatedSemanticRulesCoreWorkflowSafely()
    {
        await using var site = await FakeSite.StartAsync();
        await using var harness = await RuntimeHarness.CreateAsync(site);
        await using var server = await McpHarness.StartAsync(harness.Manager, site);
        using var anonymousClient = new HttpClient { BaseAddress = server.BaseUri };

        using (var anonymous = await anonymousClient.GetAsync("/mcp"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            var body = await anonymous.Content.ReadAsStringAsync();
            Assert.DoesNotContain(RuntimeApiToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain(site.OperatorToken, body, StringComparison.Ordinal);
            Assert.DoesNotContain("normal-site-session", body, StringComparison.Ordinal);
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.BaseUri, "/mcp"),
            Name = "operator-runtime-integration-test",
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            OwnsSession = false,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {RuntimeApiToken}"
            }
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        var toolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(
            toolNames,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "browser.status",
                "browser.navigate",
                "browser.snapshot",
                "browser.click",
                "browser.fill",
                "browser.press",
                "browser.select",
                "browser.set_checked",
                "browser.screenshot",
                "browser.console",
                "browser.network_errors"
            });

        var clickDescription = Assert.Single(tools, tool => tool.Name == "browser.click").Description;
        Assert.Contains("latest browser.snapshot", clickDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never automatically replays", clickDescription, StringComparison.OrdinalIgnoreCase);

        var status = Structured(await client.CallToolAsync("browser.status"));
        Assert.True(status.GetProperty("authenticatedSessionAvailable").GetBoolean());
        Assert.Equal(site.TestUserId, status.GetProperty("operatorUserId").GetGuid());

        AssertSuccess(await client.CallToolAsync(
            "browser.navigate",
            Args(("url", "/tools/rules-core"))));

        var rulesHome = Structured(await client.CallToolAsync("browser.snapshot"));
        var queueRef = ElementRef(rulesHome, "Adjudication Queue");
        AssertSuccess(await client.CallToolAsync(
            "browser.click",
            Args(("elementRef", queueRef))));

        var queue = Structured(await client.CallToolAsync("browser.snapshot"));
        var outcomeRef = ElementRef(queue, "Ruling outcome");
        var staleRationaleRef = ElementRef(queue, "Rationale or focused human question");

        AssertSuccess(await client.CallToolAsync(
            "browser.select",
            Args(
                ("elementRef", outcomeRef),
                ("value", null),
                ("label", "Ask a human"))));

        var staleFill = Structured(await client.CallToolAsync(
            "browser.fill",
            Args(
                ("elementRef", staleRationaleRef),
                ("value", "This reference should have been invalidated."))));
        Assert.False(staleFill.GetProperty("success").GetBoolean());
        Assert.False(staleFill.GetProperty("actionReplayed").GetBoolean());

        queue = Structured(await client.CallToolAsync("browser.snapshot"));
        var outcome = Element(queue, "Ruling outcome");
        Assert.Equal("clarify", outcome.GetProperty("value").GetString());
        Assert.Contains(
            outcome.GetProperty("options").EnumerateArray(),
            option => option.GetProperty("label").GetString() == "Ask a human"
                && option.GetProperty("selected").GetBoolean());

        var rationaleRef = ElementRef(queue, "Rationale or focused human question");
        AssertSuccess(await client.CallToolAsync(
            "browser.fill",
            Args(
                ("elementRef", rationaleRef),
                ("value", "Which source grant should control this ambiguous candidate?"))));

        queue = Structured(await client.CallToolAsync("browser.snapshot"));
        var evidenceRef = ElementRef(queue, "Evidence reviewed");
        AssertSuccess(await client.CallToolAsync(
            "browser.set_checked",
            Args(
                ("elementRef", evidenceRef),
                ("isChecked", true))));

        queue = Structured(await client.CallToolAsync("browser.snapshot"));
        Assert.True(Element(queue, "Evidence reviewed").GetProperty("checked").GetBoolean());

        var confidenceRef = ElementRef(queue, "High confidence");
        AssertSuccess(await client.CallToolAsync(
            "browser.set_checked",
            Args(
                ("elementRef", confidenceRef),
                ("isChecked", true))));

        queue = Structured(await client.CallToolAsync("browser.snapshot"));
        Assert.True(Element(queue, "High confidence").GetProperty("checked").GetBoolean());
        var recordRef = ElementRef(queue, "Record ruling");
        var recorded = AssertSuccess(await client.CallToolAsync(
            "browser.click",
            Args(("elementRef", recordRef))));
        Assert.Equal("Ruling Recorded", recorded.GetProperty("title").GetString());
        Assert.Equal(1, site.RulingCount);

        AssertSuccess(await client.CallToolAsync(
            "browser.navigate",
            Args(("url", "/"))));
        var home = Structured(await client.CallToolAsync("browser.snapshot"));
        var searchRef = ElementRef(home, "Search");
        AssertSuccess(await client.CallToolAsync(
            "browser.fill",
            Args(
                ("elementRef", searchRef),
                ("value", "semantic form submission"))));
        home = Structured(await client.CallToolAsync("browser.snapshot"));
        searchRef = ElementRef(home, "Search");
        var pressed = AssertSuccess(await client.CallToolAsync(
            "browser.press",
            Args(
                ("elementRef", searchRef),
                ("key", "Enter"))));
        Assert.Equal("Submitted", pressed.GetProperty("title").GetString());

        var screenshot = Structured(await client.CallToolAsync("browser.screenshot"));
        Assert.Equal("image/png", screenshot.GetProperty("contentType").GetString());
        Assert.True(screenshot.GetProperty("byteLength").GetInt32() > 100);

        var foreign = Structured(await client.CallToolAsync(
            "browser.navigate",
            Args(("url", "https://example.com/"))));
        Assert.False(foreign.GetProperty("success").GetBoolean());
        Assert.False(foreign.GetProperty("actionReplayed").GetBoolean());
        Assert.Contains(
            "configured Dorks & Dice Site origin",
            foreign.GetProperty("error").GetString(),
            StringComparison.Ordinal);

        var bootstrapsBeforeRecovery =
            site.Requests.Count(request => request.Path == "/operator/v1/browser-bootstrap");
        await harness.Manager.ForceContextLossForTestAsync();
        AssertSuccess(await client.CallToolAsync(
            "browser.navigate",
            Args(("url", "/"))));
        Assert.True(
            site.Requests.Count(request => request.Path == "/operator/v1/browser-bootstrap")
            > bootstrapsBeforeRecovery);

        AssertSuccess(await client.CallToolAsync(
            "browser.navigate",
            Args(("url", "/mutating"))));
        var mutating = Structured(await client.CallToolAsync("browser.snapshot"));
        var mutateRef = ElementRef(mutating, "Mutate once");
        var mutationFailure = Structured(await client.CallToolAsync(
            "browser.click",
            Args(("elementRef", mutateRef))));
        Assert.False(mutationFailure.GetProperty("success").GetBoolean());
        Assert.True(mutationFailure.GetProperty("sessionRecovered").GetBoolean());
        Assert.False(mutationFailure.GetProperty("actionReplayed").GetBoolean());
        Assert.Equal(1, site.MutationCount);

        AssertSuccess(await client.CallToolAsync(
            "browser.navigate",
            Args(("url", "/"))));
        Assert.Equal(1, site.MutationCount);

        AssertSuccess(await client.CallToolAsync(
            "browser.navigate",
            Args(("url", "/diagnostics"))));
        await Task.Delay(300);
        var console = await client.CallToolAsync("browser.console");
        var networkErrors = await client.CallToolAsync("browser.network_errors");
        var diagnostics = JsonSerializer.Serialize(new
        {
            Console = console.StructuredContent,
            Network = networkErrors.StructuredContent
        });
        Assert.Contains("operator-runtime-console-marker", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("network-secret", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeApiToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(site.OperatorToken, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("normal-site-session", diagnostics, StringComparison.Ordinal);

        Assert.All(
            site.Requests.Where(request => !request.Path.StartsWith("/operator/v1", StringComparison.Ordinal)),
            request => Assert.True(string.IsNullOrEmpty(request.Authorization)));
    }

    private static JsonElement AssertSuccess(CallToolResult result)
    {
        var structured = Structured(result);
        Assert.True(structured.GetProperty("success").GetBoolean());
        Assert.False(structured.GetProperty("actionReplayed").GetBoolean());
        return structured;
    }

    private static JsonElement Structured(CallToolResult result) =>
        result.StructuredContent
        ?? throw new Xunit.Sdk.XunitException("MCP tool result did not contain structured content.");

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    private static JsonElement Element(JsonElement snapshot, string name) =>
        Assert.Single(
            snapshot.GetProperty("elements").EnumerateArray(),
            element => element.GetProperty("name").GetString() == name);

    private static string ElementRef(JsonElement snapshot, string name) =>
        Element(snapshot, name).GetProperty("ref").GetString()
        ?? throw new Xunit.Sdk.XunitException($"Element '{name}' did not contain a ref.");

    private sealed class McpHarness(
        WebApplication app,
        Uri baseUri) : IAsyncDisposable
    {
        public Uri BaseUri { get; } = baseUri;

        public static async Task<McpHarness> StartAsync(
            BrowserSessionManager manager,
            FakeSite site)
        {
            var options = new OperatorRuntimeOptions
            {
                SiteUri = site.SiteUri,
                OperatorToken = site.OperatorToken,
                RuntimeApiToken = RuntimeApiToken,
                BrowserTimeout = TimeSpan.FromSeconds(10),
                Headless = true
            };

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<IBrowserOperations>(manager);
            builder.Services
                .AddMcpServer()
                .WithHttpTransport(http =>
                {
                    http.SessionMode = HttpServerSessionMode.Stateless;
                })
                .WithTools<BrowserMcpTools>();

            var app = builder.Build();
            app.UseMiddleware<RuntimeApiAuthenticationMiddleware>();
            app.MapMcp("/mcp");
            await app.StartAsync();

            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                ?? throw new InvalidOperationException("MCP test server did not expose a listening address.");
            return new McpHarness(
                app,
                new Uri(addresses.Single().TrimEnd('/') + "/"));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RuntimeHarness(
        HttpClient httpClient,
        BrowserSessionManager manager) : IAsyncDisposable
    {
        public BrowserSessionManager Manager { get; } = manager;

        public static async Task<RuntimeHarness> CreateAsync(FakeSite site)
        {
            var options = new OperatorRuntimeOptions
            {
                SiteUri = site.SiteUri,
                OperatorToken = site.OperatorToken,
                RuntimeApiToken = RuntimeApiToken,
                BrowserTimeout = TimeSpan.FromSeconds(10),
                Headless = true
            };
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            };
            var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            var operatorClient = new OperatorBootstrapClient(
                httpClient,
                options,
                NullLogger<OperatorBootstrapClient>.Instance);
            var manager = new BrowserSessionManager(
                options,
                operatorClient,
                new NavigationPolicy(options),
                NullLogger<BrowserSessionManager>.Instance);

            await manager.InitializeAsync();
            return new RuntimeHarness(httpClient, manager);
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            httpClient.Dispose();
        }
    }
}
