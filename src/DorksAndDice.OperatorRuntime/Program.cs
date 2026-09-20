using DorksAndDice.OperatorRuntime.AgentProtocol;
using DorksAndDice.OperatorRuntime.Api;
using DorksAndDice.OperatorRuntime.Browser;
using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Security;
using DorksAndDice.OperatorRuntime.Site;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var smokeMode = args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
var smokeRulesCore = args.Contains("--rules-core", StringComparer.OrdinalIgnoreCase);

var builder = WebApplication.CreateBuilder(args);
var runtimeOptions = OperatorRuntimeOptions.FromConfiguration(builder.Configuration, smokeMode);
builder.Services.AddSingleton(runtimeOptions);
builder.Services.AddSingleton<NavigationPolicy>();
builder.Services.AddHttpClient<OperatorBootstrapClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(15);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });
builder.Services.AddSingleton<BrowserSessionManager>();
builder.Services.AddSingleton<IBrowserOperations>(services =>
    services.GetRequiredService<BrowserSessionManager>());
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.Stateless;
    })
    .WithTools<BrowserMcpTools>();

var app = builder.Build();
app.UseMiddleware<RuntimeApiAuthenticationMiddleware>();

var browser = app.Services.GetRequiredService<BrowserSessionManager>();
try
{
    await browser.InitializeAsync(app.Lifetime.ApplicationStopping);

    if (smokeMode)
    {
        var status = await browser.GetStatusAsync();
        Console.WriteLine($"Operator: {status.OperatorDisplayName} ({status.OperatorUserId})");
        await browser.NavigateAsync("/");
        Console.WriteLine("Site home navigation: OK");

        if (smokeRulesCore)
        {
            await browser.NavigateAsync("/tools/rules-core");
            Console.WriteLine("Rules Core navigation: OK");
        }

        Console.WriteLine("Operator Runtime smoke test: OK");
        return;
    }

    app.MapGet("/health/live", () => Results.Ok(new
    {
        processAlive = true
    }));

    app.MapGet("/health/ready", async (IBrowserOperations operations, CancellationToken ct) =>
    {
        var status = await operations.GetStatusAsync(ct);
        var body = new
        {
            processAlive = true,
            browserInitialized = status.PlaywrightInitialized && status.ChromiumRunning,
            siteSessionAvailable = status.AuthenticatedSessionAvailable
        };

        return body.browserInitialized && body.siteSessionAvailable
            ? Results.Ok(body)
            : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
    });

    app.MapBrowserApi();
    app.MapMcp("/mcp");

    await app.RunAsync();
}
finally
{
    await browser.DisposeAsync();
}

public partial class Program;
