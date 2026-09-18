namespace DorksAndDice.OperatorRuntime.Browser;

public sealed record BrowserStatus(
    bool PlaywrightInitialized,
    bool ChromiumRunning,
    bool AuthenticatedSessionAvailable,
    string? CurrentUrl,
    string? PageTitle,
    Guid? OperatorUserId,
    string? OperatorDisplayName);

public sealed record SnapshotElement(
    string Ref,
    string Role,
    string? Name,
    string? Type,
    bool Disabled);

public sealed record BrowserSnapshot(
    string Url,
    string Title,
    IReadOnlyList<string> Headings,
    string Text,
    IReadOnlyList<SnapshotElement> Elements);

public sealed record BrowserActionResult(
    bool Success,
    string Url,
    string Title,
    string? Message = null,
    bool SessionRecovered = false);

public sealed record BrowserScreenshot(
    string ContentType,
    string Base64,
    int ByteLength);

public sealed record BrowserConsoleEntry(
    DateTimeOffset Timestamp,
    string Type,
    string Text);

public sealed record BrowserNetworkError(
    DateTimeOffset Timestamp,
    string Method,
    string Url,
    int? Status,
    string Kind);

public sealed record NavigateRequest(string Url);
public sealed record ClickRequest(string Ref);
public sealed record FillRequest(string Ref, string Value);
public sealed record PressRequest(string? Ref, string Key);

public interface IBrowserOperations
{
    Task<BrowserStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<BrowserActionResult> NavigateAsync(string target, CancellationToken cancellationToken = default);
    Task<BrowserSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
    Task<BrowserActionResult> ClickAsync(string elementRef, CancellationToken cancellationToken = default);
    Task<BrowserActionResult> FillAsync(string elementRef, string value, CancellationToken cancellationToken = default);
    Task<BrowserActionResult> PressAsync(string? elementRef, string key, CancellationToken cancellationToken = default);
    Task<BrowserScreenshot> ScreenshotAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<BrowserConsoleEntry> GetConsole();
    IReadOnlyList<BrowserNetworkError> GetNetworkErrors();
}

public sealed class BrowserOperationException(
    string message,
    bool sessionRecovered,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool SessionRecovered { get; } = sessionRecovered;
}

internal sealed class BrowserAuthenticationLostException : Exception
{
    public BrowserAuthenticationLostException()
        : base("The Site browser session is no longer authenticated.")
    {
    }
}
