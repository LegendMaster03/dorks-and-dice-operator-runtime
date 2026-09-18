using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using DorksAndDice.OperatorRuntime.Api;
using DorksAndDice.OperatorRuntime.Browser;
using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Site;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DorksAndDice.OperatorRuntime.Tests;

public sealed class BrowserRuntimeIntegrationTests
{
    [Fact]
    public async Task RejectedOperatorCredentialPreventsBrowserSessionCreation()
    {
        await using var site = await FakeSite.StartAsync(rejectOperator: true);
        await using var harness = await RuntimeHarness.CreateAsync(site, initialize: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Manager.InitializeAsync());
        var status = await harness.Manager.GetStatusAsync();

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.False(status.PlaywrightInitialized);
        Assert.False(status.AuthenticatedSessionAvailable);
    }

    [Fact]
    public async Task BootstrapNavigationFailureDoesNotExposeOneUseToken()
    {
        const string bootstrapSecret = "bootstrap-token-must-not-leak";
        var options = new OperatorRuntimeOptions
        {
            SiteUri = new Uri("http://127.0.0.1:9/"),
            OperatorToken = "ddop_v1_test_operator_secret",
            RuntimeApiToken = "runtime-api-test-secret",
            BrowserTimeout = TimeSpan.FromSeconds(3),
            Headless = true
        };

        using var httpClient = new HttpClient(new OperatorFixtureHandler(bootstrapSecret))
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        var operatorClient = new OperatorBootstrapClient(
            httpClient,
            options,
            NullLogger<OperatorBootstrapClient>.Instance);
        await using var manager = new BrowserSessionManager(
            options,
            operatorClient,
            new NavigationPolicy(options),
            NullLogger<BrowserSessionManager>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InitializeAsync());

        Assert.DoesNotContain(bootstrapSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token=", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Browser bootstrap navigation failed.", exception.Message);
    }

    [Fact]
    public async Task TopLevelOriginBoundaryBlocksRedirectLinksFormsScriptAndPopups()
    {
        await using var site = await FakeSite.StartAsync();
        await using var harness = await RuntimeHarness.CreateAsync(site);
        var manager = harness.Manager;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.NavigateAsync("https://example.com/"));

        var redirectFailure = await Assert.ThrowsAsync<BrowserOperationException>(
            () => manager.NavigateAsync("/external-redirect"));
        Assert.Contains("top-level navigation", redirectFailure.Message, StringComparison.OrdinalIgnoreCase);
        await AssertTrustedSessionAsync(manager, site);

        var sameOriginRedirect = await manager.NavigateAsync("/same-origin-redirect");
        Assert.EndsWith("/same-origin-destination", sameOriginRedirect.Url, StringComparison.Ordinal);
        Assert.Equal("Same Origin", sameOriginRedirect.Title);

        await manager.NavigateAsync("/navigation-fixture");
        var linkSnapshot = await manager.SnapshotAsync();
        var externalLink = Assert.Single(linkSnapshot.Elements, element => element.Name == "External link");
        var linkFailure = await Assert.ThrowsAsync<BrowserOperationException>(
            () => manager.ClickAsync(externalLink.Ref));
        Assert.Contains("top-level navigation", linkFailure.Message, StringComparison.OrdinalIgnoreCase);
        await AssertTrustedSessionAsync(manager, site);

        await manager.NavigateAsync("/navigation-fixture");
        var formSnapshot = await manager.SnapshotAsync();
        var externalForm = Assert.Single(formSnapshot.Elements, element => element.Name == "External form submit");
        var formFailure = await Assert.ThrowsAsync<BrowserOperationException>(
            () => manager.ClickAsync(externalForm.Ref));
        Assert.Contains("top-level navigation", formFailure.Message, StringComparison.OrdinalIgnoreCase);
        await AssertTrustedSessionAsync(manager, site);

        await manager.NavigateAsync("/navigation-fixture");
        var popupSnapshot = await manager.SnapshotAsync();
        var popup = Assert.Single(popupSnapshot.Elements, element => element.Name == "External popup");
        var popupFailure = await Assert.ThrowsAsync<BrowserOperationException>(
            () => manager.ClickAsync(popup.Ref));
        Assert.Contains("popup", popupFailure.Message, StringComparison.OrdinalIgnoreCase);
        await AssertTrustedSessionAsync(manager, site);
        Assert.Equal(1, manager.OpenPageCountForTest);

        await manager.NavigateAsync("/navigation-fixture");
        var scriptSnapshot = await manager.SnapshotAsync();
        var scriptNavigation = Assert.Single(
            scriptSnapshot.Elements,
            element => element.Name == "External script navigation");
        await Assert.ThrowsAsync<BrowserOperationException>(
            () => manager.ClickAsync(scriptNavigation.Ref));
        await AssertTrustedSessionAsync(manager, site);

        await manager.NavigateAsync("/navigation-fixture");
        var sameLinkSnapshot = await manager.SnapshotAsync();
        var sameOriginLink = Assert.Single(sameLinkSnapshot.Elements, element => element.Name == "Same-origin link");
        var sameOriginLinkResult = await manager.ClickAsync(sameOriginLink.Ref);
        Assert.EndsWith("/same-origin-destination", sameOriginLinkResult.Url, StringComparison.Ordinal);

        await manager.NavigateAsync("/navigation-fixture");
        var sameFormSnapshot = await manager.SnapshotAsync();
        var sameSearch = Assert.Single(sameFormSnapshot.Elements, element => element.Name == "Same-origin search");
        await manager.FillAsync(sameSearch.Ref, "same-origin");
        var refreshedFormSnapshot = await manager.SnapshotAsync();
        var sameSubmit = Assert.Single(
            refreshedFormSnapshot.Elements,
            element => element.Name == "Same-origin form submit");
        var sameFormResult = await manager.ClickAsync(sameSubmit.Ref);
        Assert.EndsWith("/submitted", sameFormResult.Url, StringComparison.Ordinal);

        var rulesCore = await manager.NavigateAsync("/tools/rules-core");
        Assert.EndsWith("/tools/rules-core", rulesCore.Url, StringComparison.Ordinal);
        Assert.Equal("Rules Core", rulesCore.Title);
    }

    [Fact]
    public async Task CrossOriginSubresourcesRemainAllowed()
    {
        await using var site = await FakeSite.StartAsync();
        await using var harness = await RuntimeHarness.CreateAsync(site);
        var manager = harness.Manager;

        var navigation = await manager.NavigateAsync("/cross-origin-subresource");
        Assert.EndsWith("/cross-origin-subresource", navigation.Url, StringComparison.Ordinal);

        var snapshot = await manager.SnapshotAsync();
        Assert.Contains("cross-origin-loaded", snapshot.Text, StringComparison.Ordinal);
        await AssertTrustedSessionAsync(manager, site);
    }

    [Fact]
    public async Task SnapshotContainsOnlyVisibleUsableControlsWithAccessibleNames()
    {
        await using var site = await FakeSite.StartAsync();
        await using var harness = await RuntimeHarness.CreateAsync(site);
        var manager = harness.Manager;

        await manager.NavigateAsync("/snapshot-fixture");
        var snapshot = await manager.SnapshotAsync();

        Assert.Single(snapshot.Elements, element => element.Name == "Responsive action");

        var explicitLabel = Assert.Single(snapshot.Elements, element => element.Name == "Explicit Search");
        Assert.Equal("textbox", explicitLabel.Role);

        var wrappedLabel = Assert.Single(snapshot.Elements, element => element.Name == "Wrapped Search");
        Assert.Equal("textbox", wrappedLabel.Role);

        var labelledBy = Assert.Single(snapshot.Elements, element => element.Name == "Save changes");
        Assert.Equal("button", labelledBy.Role);

        var ariaLabel = Assert.Single(snapshot.Elements, element => element.Name == "ARIA Save");
        Assert.Equal("button", ariaLabel.Role);
        Assert.DoesNotContain(snapshot.Elements, element => element.Name == "Inner text should not win");

        var disabled = Assert.Single(snapshot.Elements, element => element.Name == "Disabled action");
        Assert.True(disabled.Disabled);
    }

    [Fact]
    public async Task FillInvalidatesPreviousSnapshotReferences()
    {
        await using var site = await FakeSite.StartAsync();
        await using var harness = await RuntimeHarness.CreateAsync(site);
        var manager = harness.Manager;

        await manager.NavigateAsync("/");
        var snapshot = await manager.SnapshotAsync();
        var search = Assert.Single(snapshot.Elements, element => element.Name == "Search");

        await manager.FillAsync(search.Ref, "Alice");

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => manager.PressAsync(search.Ref, "Enter"));

        var refreshed = await manager.SnapshotAsync();
        var refreshedSearch = Assert.Single(refreshed.Elements, element => element.Name == "Search");
        var submitted = await manager.PressAsync(refreshedSearch.Ref, "Enter");
        Assert.EndsWith("/submitted", submitted.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignOriginFailureUsesStructuredApiConflictWithoutReplay()
    {
        await using var site = await FakeSite.StartAsync();
        await using var harness = await RuntimeHarness.CreateAsync(site);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IBrowserOperations>(harness.Manager);
        await using var app = builder.Build();
        app.MapBrowserApi();
        await app.StartAsync();

        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            ?? throw new InvalidOperationException("Test API did not expose a listening address.");
        using var client = new HttpClient
        {
            BaseAddress = new Uri(addresses.Single().TrimEnd('/') + "/")
        };

        using var response = await client.PostAsJsonAsync(
            "/api/v1/browser/navigate",
            new NavigateRequest("/external-redirect"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            "top-level navigation",
            body.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(body.GetProperty("actionReplayed").GetBoolean());

        await AssertTrustedSessionAsync(harness.Manager, site);
        await app.StopAsync();
    }

    [Fact]
    public async Task CompleteBootstrapAndBrowserControlFlowIsSafeAndRecoverable()
    {
        await using var site = await FakeSite.StartAsync();
        await using var harness = await RuntimeHarness.CreateAsync(site);
        var manager = harness.Manager;

        var status = await manager.GetStatusAsync();
        Assert.True(status.PlaywrightInitialized);
        Assert.True(status.ChromiumRunning);
        Assert.True(status.AuthenticatedSessionAvailable);
        Assert.Equal(site.TestUserId, status.OperatorUserId);
        Assert.Equal("Operator Runtime Test", status.OperatorDisplayName);

        Assert.Contains(
            site.Requests,
            request => request.Path == "/account" && request.Cookie.Contains("dd-test-auth=", StringComparison.Ordinal));
        Assert.All(
            site.Requests.Where(request => !request.Path.StartsWith("/operator/v1", StringComparison.Ordinal)),
            request => Assert.True(string.IsNullOrEmpty(request.Authorization)));

        Assert.NotNull(site.LastBootstrapUrl);
        using (var reuseClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        using (var reuse = await reuseClient.GetAsync(new Uri(site.SiteUri, site.LastBootstrapUrl)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        }

        var rulesCore = await manager.NavigateAsync("/tools/rules-core");
        Assert.True(rulesCore.Success);
        Assert.EndsWith("/tools/rules-core", rulesCore.Url, StringComparison.Ordinal);
        Assert.Equal("Rules Core", rulesCore.Title);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.NavigateAsync("https://example.com/"));

        await manager.NavigateAsync("/");
        var snapshot = await manager.SnapshotAsync();
        Assert.Contains("Runtime Test Home", snapshot.Headings);
        Assert.Contains(snapshot.Elements, element => element.Role == "link" && element.Name == "Rules Core");
        var increment = Assert.Single(snapshot.Elements, element => element.Name == "Increment");
        Assert.Equal("button", increment.Role);

        await manager.ClickAsync(increment.Ref);
        var afterClick = await manager.SnapshotAsync();
        Assert.Contains(" 1", " " + afterClick.Text, StringComparison.Ordinal);

        var search = Assert.Single(afterClick.Elements, element => element.Name == "Search");
        Assert.Equal("textbox", search.Role);
        await manager.FillAsync(search.Ref, "Alice");
        var afterFill = await manager.SnapshotAsync();
        var refreshedSearch = Assert.Single(afterFill.Elements, element => element.Name == "Search");
        var afterPress = await manager.PressAsync(refreshedSearch.Ref, "Enter");
        Assert.EndsWith("/submitted", afterPress.Url, StringComparison.Ordinal);
        Assert.Equal("Submitted", afterPress.Title);

        var screenshot = await manager.ScreenshotAsync();
        Assert.Equal("image/png", screenshot.ContentType);
        Assert.True(screenshot.ByteLength > 100);
        Assert.Equal(screenshot.ByteLength, Convert.FromBase64String(screenshot.Base64).Length);

        await manager.NavigateAsync("/diagnostics");
        await Task.Delay(300);
        Assert.Contains(
            manager.GetConsole(),
            entry => entry.Text.Contains("operator-runtime-console-marker", StringComparison.Ordinal));

        var networkErrors = manager.GetNetworkErrors();
        Assert.Contains(networkErrors, entry => entry.Status == 404 && entry.Url.EndsWith("/missing", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "network-secret",
            System.Text.Json.JsonSerializer.Serialize(networkErrors),
            StringComparison.Ordinal);

        var bootstrapsBeforeContextLoss =
            site.Requests.Count(request => request.Path == "/operator/v1/browser-bootstrap");
        await manager.ForceContextLossForTestAsync();
        var contextRecovery = await manager.NavigateAsync("/");
        Assert.True(contextRecovery.Success);
        Assert.True(
            site.Requests.Count(request => request.Path == "/operator/v1/browser-bootstrap")
            > bootstrapsBeforeContextLoss);

        await manager.NavigateAsync("/mutating");
        var mutatingSnapshot = await manager.SnapshotAsync();
        var mutate = Assert.Single(mutatingSnapshot.Elements, element => element.Name == "Mutate once");

        var bootstrapsBeforeMutation =
            site.Requests.Count(request => request.Path == "/operator/v1/browser-bootstrap");
        var mutationFailure = await Assert.ThrowsAsync<BrowserOperationException>(
            () => manager.ClickAsync(mutate.Ref));

        Assert.True(mutationFailure.SessionRecovered);
        Assert.Equal(1, site.MutationCount);
        Assert.True(
            site.Requests.Count(request => request.Path == "/operator/v1/browser-bootstrap")
            > bootstrapsBeforeMutation);

        var recovered = await manager.GetStatusAsync();
        Assert.True(recovered.AuthenticatedSessionAvailable);

        await manager.NavigateAsync("/");
        Assert.Equal(1, site.MutationCount);

        Assert.All(
            site.Requests.Where(request => !request.Path.StartsWith("/operator/v1", StringComparison.Ordinal)),
            request => Assert.True(string.IsNullOrEmpty(request.Authorization)));
    }

    private static async Task AssertTrustedSessionAsync(BrowserSessionManager manager, FakeSite site)
    {
        var status = await manager.GetStatusAsync();
        Assert.True(status.AuthenticatedSessionAvailable);
        Assert.NotNull(status.CurrentUrl);
        Assert.True(
            status.CurrentUrl.StartsWith(
                site.SiteUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase));
    }

    private sealed class OperatorFixtureHandler(string bootstrapSecret) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/operator/v1/me")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        userId = Guid.Parse("7f1f78e8-6f61-42db-988a-2333a0e5eafd"),
                        displayName = "Operator Runtime Test",
                        accountKind = "ServicePrincipal",
                        globalRoles = Array.Empty<string>()
                    })
                });
            }

            if (request.RequestUri.AbsolutePath == "/operator/v1/browser-bootstrap")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        bootstrapId = Guid.NewGuid(),
                        bootstrapUrl = $"/operator/bootstrap?token={bootstrapSecret}",
                        expiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RuntimeHarness(
        HttpClient httpClient,
        BrowserSessionManager manager) : IAsyncDisposable
    {
        public BrowserSessionManager Manager { get; } = manager;

        public static async Task<RuntimeHarness> CreateAsync(FakeSite site, bool initialize = true)
        {
            var options = new OperatorRuntimeOptions
            {
                SiteUri = site.SiteUri,
                OperatorToken = site.OperatorToken,
                RuntimeApiToken = "runtime-api-test-secret",
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

            if (initialize)
            {
                await manager.InitializeAsync();
            }

            return new RuntimeHarness(httpClient, manager);
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            httpClient.Dispose();
        }
    }
}
