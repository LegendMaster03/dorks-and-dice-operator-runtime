using System.Text.Json;
using DorksAndDice.OperatorRuntime.AgentProtocol;
using DorksAndDice.OperatorRuntime.Browser;
using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Security;
using Json.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DorksAndDice.OperatorRuntime.Tests;

public sealed class StructuredOutputSchemaTests
{
    private const string RuntimeApiToken = "runtime-api-test-secret";

    [Fact]
    public async Task StructuredToolResultsConformToAdvertisedOutputSchemasIncludingNullMembers()
    {
        await using var server = await McpHarness.StartAsync(new NullableBrowserOperations());

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.BaseUri, "/mcp"),
            Name = "operator-runtime-structured-output-test",
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            OwnsSession = false,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {RuntimeApiToken}"
            }
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = (await client.ListToolsAsync())
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        async Task<JsonElement> CallAndValidateAsync(
            string toolName,
            Dictionary<string, object?>? arguments = null)
        {
            var result = arguments is null
                ? await client.CallToolAsync(toolName)
                : await client.CallToolAsync(toolName, arguments);

            return AssertConforms(tools[toolName], result);
        }

        var status = await CallAndValidateAsync("browser.status");
        Assert.Equal(JsonValueKind.Null, status.GetProperty("currentUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pageTitle").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("operatorUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("operatorDisplayName").ValueKind);

        var navigate = await CallAndValidateAsync(
            "browser.navigate",
            Args(("url", "/")));
        Assert.True(navigate.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, navigate.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.Null, navigate.GetProperty("error").ValueKind);
        Assert.False(navigate.GetProperty("actionReplayed").GetBoolean());

        var failedNavigate = await CallAndValidateAsync(
            "browser.navigate",
            Args(("url", "/fail")));
        Assert.False(failedNavigate.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, failedNavigate.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, failedNavigate.GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, failedNavigate.GetProperty("message").ValueKind);
        Assert.Contains(
            "Synthetic navigation failure.",
            failedNavigate.GetProperty("error").GetString(),
            StringComparison.Ordinal);

        var snapshot = await CallAndValidateAsync("browser.snapshot");
        var element = Assert.Single(snapshot.GetProperty("elements").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, element.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("type").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("checked").ValueKind);
        Assert.Equal(JsonValueKind.Null, element.GetProperty("options").ValueKind);

        await CallAndValidateAsync("browser.click", Args(("elementRef", "element-1")));
        await CallAndValidateAsync(
            "browser.fill",
            Args(("elementRef", "element-1"), ("value", "value")));
        await CallAndValidateAsync(
            "browser.press",
            Args(("elementRef", null), ("key", "Enter")));
        await CallAndValidateAsync(
            "browser.select",
            Args(("elementRef", "element-1"), ("value", "value"), ("label", null)));
        await CallAndValidateAsync(
            "browser.set_checked",
            Args(("elementRef", "element-1"), ("isChecked", true)));

        await CallAndValidateAsync("browser.console");

        var networkErrors = await CallAndValidateAsync("browser.network_errors");
        var networkError = Assert.Single(networkErrors.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, networkError.GetProperty("status").ValueKind);

        Assert.Null(tools["browser.screenshot"].ProtocolTool.OutputSchema);
    }

    private static JsonElement AssertConforms(McpClientTool tool, CallToolResult result)
    {
        var structured = result.StructuredContent
            ?? throw new Xunit.Sdk.XunitException(
                $"MCP tool '{tool.Name}' did not return structured content.");

        var outputSchema = tool.ProtocolTool.OutputSchema
            ?? throw new Xunit.Sdk.XunitException(
                $"MCP tool '{tool.Name}' did not advertise an output schema.");

        var schema = JsonSchema.FromText(outputSchema.GetRawText());
        var evaluation = schema.Evaluate(structured);

        Assert.True(
            evaluation.IsValid,
            $"MCP tool '{tool.Name}' returned structured content that does not conform to its advertised output schema.\n" +
            $"Output schema: {outputSchema.GetRawText()}\n" +
            $"Structured content: {structured.GetRawText()}");

        return structured;
    }

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);

    private sealed class McpHarness(
        WebApplication app,
        Uri baseUri) : IAsyncDisposable
    {
        public Uri BaseUri { get; } = baseUri;

        public static async Task<McpHarness> StartAsync(IBrowserOperations browser)
        {
            var options = new OperatorRuntimeOptions
            {
                SiteUri = new Uri("https://dorks-and-dice.test/"),
                OperatorToken = "operator-test-secret",
                RuntimeApiToken = RuntimeApiToken,
                BrowserTimeout = TimeSpan.FromSeconds(10),
                Headless = true
            };

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(browser);
            builder.Services.AddSingleton<IBrowserOperations>(browser);
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

    private sealed class NullableBrowserOperations : IBrowserOperations
    {
        public Task<BrowserStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowserStatus(
                true,
                true,
                false,
                null,
                null,
                null,
                null));

        public Task<BrowserActionResult> NavigateAsync(
            string target,
            CancellationToken cancellationToken = default)
        {
            if (target == "/fail")
            {
                throw new ArgumentException("Synthetic navigation failure.", nameof(target));
            }

            return Task.FromResult(Success());
        }

        public Task<BrowserSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowserSnapshot(
                "https://dorks-and-dice.test/",
                "Home",
                [],
                "Home",
                [
                    new SnapshotElement(
                        "element-1",
                        "button",
                        null,
                        null,
                        false,
                        null,
                        null,
                        null)
                ]));

        public Task<BrowserActionResult> ClickAsync(
            string elementRef,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<BrowserActionResult> FillAsync(
            string elementRef,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<BrowserActionResult> PressAsync(
            string? elementRef,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<BrowserActionResult> SelectOptionAsync(
            string elementRef,
            string? value,
            string? label,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<BrowserActionResult> SetCheckedAsync(
            string elementRef,
            bool isChecked,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Success());

        public Task<BrowserScreenshot> ScreenshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BrowserScreenshot(
                "image/png",
                Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47]),
                4));

        public IReadOnlyList<BrowserConsoleEntry> GetConsole() =>
            [
                new BrowserConsoleEntry(
                    DateTimeOffset.UnixEpoch,
                    "log",
                    "console")
            ];

        public IReadOnlyList<BrowserNetworkError> GetNetworkErrors() =>
            [
                new BrowserNetworkError(
                    DateTimeOffset.UnixEpoch,
                    "GET",
                    "/failed",
                    null,
                    "requestfailed")
            ];

        private static BrowserActionResult Success() =>
            new(
                true,
                "https://dorks-and-dice.test/",
                "Home",
                null,
                false);
    }
}
