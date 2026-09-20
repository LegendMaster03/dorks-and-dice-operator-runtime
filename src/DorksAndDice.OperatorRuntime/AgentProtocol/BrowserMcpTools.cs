using System.ComponentModel;
using DorksAndDice.OperatorRuntime.Browser;
using ModelContextProtocol.Server;

namespace DorksAndDice.OperatorRuntime.AgentProtocol;

[McpServerToolType]
public sealed class BrowserMcpTools
{
    private const string MutationGuidance =
        "Element references come from the latest browser.snapshot. Any DOM-changing operation invalidates prior references, so obtain a new snapshot afterward. " +
        "This operation may already have mutated Site state if a browser or Site session failure occurs. The runtime never automatically replays that action, and actionReplayed is always false. " +
        "Top-level navigation is restricted to the configured Dorks & Dice Site origin.";

    [McpServerTool(
        Name = "browser.status",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the controlled Chromium and authenticated Dorks & Dice Site session status. No runtime credential, Operator credential, or browser cookie is returned.")]
    public static Task<BrowserStatus> StatusAsync(
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        browser.GetStatusAsync(cancellationToken);

    [McpServerTool(
        Name = "browser.navigate",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Navigates the controlled browser. Only the configured Dorks & Dice Site origin is allowed. " + MutationGuidance)]
    public static Task<AgentBrowserActionResult> NavigateAsync(
        [Description("A Dorks & Dice Site-relative path or an absolute URL on the configured Site origin.")] string url,
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(() => browser.NavigateAsync(url, cancellationToken));

    [McpServerTool(
        Name = "browser.snapshot",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns a semantic snapshot of the current Dorks & Dice Site page. Interactive elements include ephemeral refs, accessible names, control state, and select options. Use only refs from this latest snapshot. Any later DOM-changing browser operation invalidates them.")]
    public static Task<BrowserSnapshot> SnapshotAsync(
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        browser.SnapshotAsync(cancellationToken);

    [McpServerTool(
        Name = "browser.click",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Clicks one semantic element reference from the latest browser.snapshot. " + MutationGuidance)]
    public static Task<AgentBrowserActionResult> ClickAsync(
        [Description("Element ref from the latest browser.snapshot.")] string elementRef,
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(() => browser.ClickAsync(elementRef, cancellationToken));

    [McpServerTool(
        Name = "browser.fill",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Fills a standard text-like control, including input and textarea, using a ref from the latest browser.snapshot. " + MutationGuidance)]
    public static Task<AgentBrowserActionResult> FillAsync(
        [Description("Element ref from the latest browser.snapshot.")] string elementRef,
        [Description("Complete replacement value to place in the control.")] string value,
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(() => browser.FillAsync(elementRef, value, cancellationToken));

    [McpServerTool(
        Name = "browser.press",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Presses a keyboard key on an element from the latest snapshot, or on the page when elementRef is omitted. This can submit ordinary forms with Enter. " + MutationGuidance)]
    public static Task<AgentBrowserActionResult> PressAsync(
        [Description("Optional element ref from the latest browser.snapshot.")] string? elementRef,
        [Description("Playwright keyboard key such as Enter, Tab, Escape, or ArrowDown.")] string key,
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(() => browser.PressAsync(elementRef, key, cancellationToken));

    [McpServerTool(
        Name = "browser.select",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Selects one option in a native select control. Supply exactly one of value or label; available options are reported by browser.snapshot. " + MutationGuidance)]
    public static Task<AgentBrowserActionResult> SelectAsync(
        [Description("Select element ref from the latest browser.snapshot.")] string elementRef,
        [Description("Exact option value. Supply either value or label, not both.")] string? value,
        [Description("Exact visible option label. Supply either label or value, not both.")] string? label,
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(() => browser.SelectOptionAsync(elementRef, value, label, cancellationToken));

    [McpServerTool(
        Name = "browser.set_checked",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Sets a native checkbox or radio control to the requested checked state using a ref from the latest browser.snapshot. " + MutationGuidance)]
    public static Task<AgentBrowserActionResult> SetCheckedAsync(
        [Description("Checkbox or radio element ref from the latest browser.snapshot.")] string elementRef,
        [Description("Requested checked state.")] bool isChecked,
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        ExecuteActionAsync(() => browser.SetCheckedAsync(elementRef, isChecked, cancellationToken));

    [McpServerTool(
        Name = "browser.screenshot",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns an in-memory PNG screenshot of the controlled Dorks & Dice Site page as base64. The runtime does not persist it to disk.")]
    public static Task<BrowserScreenshot> ScreenshotAsync(
        IBrowserOperations browser,
        CancellationToken cancellationToken) =>
        browser.ScreenshotAsync(cancellationToken);

    [McpServerTool(
        Name = "browser.console",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the bounded in-memory browser console diagnostic buffer. Runtime credentials and browser cookies are never intentionally inserted into page content.")]
    public static IReadOnlyList<BrowserConsoleEntry> Console(IBrowserOperations browser) =>
        browser.GetConsole();

    [McpServerTool(
        Name = "browser.network_errors",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns bounded network failures with sanitized URL paths. Query strings and request headers are omitted so runtime credentials, Operator credentials, and browser cookies are not exposed.")]
    public static IReadOnlyList<BrowserNetworkError> NetworkErrors(IBrowserOperations browser) =>
        browser.GetNetworkErrors();

    private static async Task<AgentBrowserActionResult> ExecuteActionAsync(
        Func<Task<BrowserActionResult>> operation)
    {
        try
        {
            var result = await operation();
            return new AgentBrowserActionResult(
                result.Success,
                result.Url,
                result.Title,
                result.Message,
                null,
                result.SessionRecovered,
                false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BrowserOperationException exception)
        {
            return AgentBrowserActionResult.Failure(
                exception.Message,
                exception.SessionRecovered);
        }
        catch (KeyNotFoundException exception)
        {
            return AgentBrowserActionResult.Failure(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return AgentBrowserActionResult.Failure(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return AgentBrowserActionResult.Failure(exception.Message);
        }
        catch
        {
            return AgentBrowserActionResult.Failure("The browser operation could not be completed.");
        }
    }
}

public sealed record AgentBrowserActionResult(
    bool Success,
    string? Url,
    string? Title,
    string? Message,
    string? Error,
    bool SessionRecovered,
    bool ActionReplayed)
{
    public static AgentBrowserActionResult Failure(
        string error,
        bool sessionRecovered = false) =>
        new(
            false,
            null,
            null,
            null,
            error,
            sessionRecovered,
            false);
}
