using System.Net;
using System.Net.Http.Json;
using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Site;
using Microsoft.Extensions.Logging.Abstractions;

namespace DorksAndDice.OperatorRuntime.Tests;

public sealed class SiteClientTests
{
    [Fact]
    public async Task BearerCredentialIsUsedOnlyForFixedOperatorEndpoints()
    {
        const string secret = "ddop_v1_unit_test_secret";
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/operator/v1/me")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        userId = Guid.NewGuid(),
                        displayName = "Test Operator",
                        accountKind = "ServicePrincipal",
                        globalRoles = Array.Empty<string>()
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    bootstrapId = Guid.NewGuid(),
                    bootstrapUrl = "/operator/bootstrap?token=one-use",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                })
            };
        });

        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, secret);
        await client.GetCurrentOperatorAsync();
        await client.CreateBrowserBootstrapAsync();

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/operator/v1/me", request.Path);
                Assert.Equal($"Bearer {secret}", request.Authorization);
            },
            request =>
            {
                Assert.Equal("/operator/v1/browser-bootstrap", request.Path);
                Assert.Equal($"Bearer {secret}", request.Authorization);
            });
    }

    [Fact]
    public async Task RejectedCredentialDoesNotAppearInException()
    {
        const string secret = "ddop_v1_never_print_this_secret";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient, secret);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetCurrentOperatorAsync());

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
    }

    private static OperatorBootstrapClient CreateClient(HttpClient httpClient, string secret)
    {
        var options = new OperatorRuntimeOptions
        {
            SiteUri = new Uri("https://dorks-and-dice.com/"),
            OperatorToken = secret,
            RuntimeApiToken = "runtime-secret"
        };

        return new OperatorBootstrapClient(
            httpClient,
            options,
            NullLogger<OperatorBootstrapClient>.Instance);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty));
            return Task.FromResult(responder(request));
        }
    }

    private sealed record RecordedRequest(string Path, string Authorization);
}
