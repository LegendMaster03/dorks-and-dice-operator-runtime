using System.Net;
using DorksAndDice.OperatorRuntime.Browser;
using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Site;
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
        var increment = Assert.Single(snapshot.Elements.Where(element => element.Name == "Increment"));
        Assert.Equal("button", increment.Role);

        await manager.ClickAsync(increment.Ref);
        var afterClick = await manager.SnapshotAsync();
        Assert.Contains(" 1", " " + afterClick.Text, StringComparison.Ordinal);

        var search = Assert.Single(afterClick.Elements.Where(element => element.Name == "Search"));
        Assert.Equal("textbox", search.Role);
        await manager.FillAsync(search.Ref, "Alice");
        var afterPress = await manager.PressAsync(search.Ref, "Enter");
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

        await manager.NavigateAsync("/mutating");
        var mutatingSnapshot = await manager.SnapshotAsync();
        var mutate = Assert.Single(mutatingSnapshot.Elements.Where(element => element.Name == "Mutate slowly"));

        var clickTask = manager.ClickAsync(mutate.Ref);
        await site.MutationStarted.WaitAsync(TimeSpan.FromSeconds(10));
        await manager.ForceContextLossForTestAsync();
        site.ReleaseMutation();

        var mutationFailure = await Assert.ThrowsAsync<BrowserOperationException>(() => clickTask);
        Assert.True(mutationFailure.SessionRecovered);
        Assert.Equal(1, site.MutationCount);

        var recovered = await manager.GetStatusAsync();
        Assert.True(recovered.AuthenticatedSessionAvailable);
        Assert.True(site.Requests.Count(request => request.Path == "/operator/v1/browser-bootstrap") >= 2);

        await manager.NavigateAsync("/");
        Assert.Equal(1, site.MutationCount);

        Assert.All(
            site.Requests.Where(request => !request.Path.StartsWith("/operator/v1", StringComparison.Ordinal)),
            request => Assert.True(string.IsNullOrEmpty(request.Authorization)));
    }

    private sealed class RuntimeHarness(
        FakeSite site,
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

            return new RuntimeHarness(site, httpClient, manager);
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            httpClient.Dispose();
        }
    }
}
