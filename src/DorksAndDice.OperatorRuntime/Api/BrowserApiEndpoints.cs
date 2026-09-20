using DorksAndDice.OperatorRuntime.Browser;

namespace DorksAndDice.OperatorRuntime.Api;

public static class BrowserApiEndpoints
{
    public static IEndpointRouteBuilder MapBrowserApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/browser");

        group.MapGet("/status", async (IBrowserOperations browser, CancellationToken ct) =>
            Results.Ok(await browser.GetStatusAsync(ct)));

        group.MapPost("/navigate", async (NavigateRequest request, IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.NavigateAsync(request.Url, ct)));

        group.MapGet("/snapshot", async (IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.SnapshotAsync(ct)));

        group.MapPost("/click", async (ClickRequest request, IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.ClickAsync(request.Ref, ct)));

        group.MapPost("/fill", async (FillRequest request, IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.FillAsync(request.Ref, request.Value, ct)));

        group.MapPost("/press", async (PressRequest request, IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.PressAsync(request.Ref, request.Key, ct)));

        group.MapPost("/select", async (SelectOptionRequest request, IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.SelectOptionAsync(request.Ref, request.Value, request.Label, ct)));

        group.MapPost("/set-checked", async (SetCheckedRequest request, IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.SetCheckedAsync(request.Ref, request.Checked, ct)));

        group.MapGet("/screenshot", async (IBrowserOperations browser, CancellationToken ct) =>
            await ExecuteAsync(() => browser.ScreenshotAsync(ct)));

        group.MapGet("/console", (IBrowserOperations browser) => Results.Ok(browser.GetConsole()));
        group.MapGet("/network-errors", (IBrowserOperations browser) => Results.Ok(browser.GetNetworkErrors()));

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (BrowserOperationException exception)
        {
            return Results.Json(
                new
                {
                    error = exception.Message,
                    sessionRecovered = exception.SessionRecovered,
                    actionReplayed = false
                },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}
