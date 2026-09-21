using System.Text.Json.Serialization;

namespace DorksAndDice.OperatorRuntime.Browser;

public sealed record BrowserStatus(
    bool PlaywrightInitialized,
    bool ChromiumRunning,
    bool AuthenticatedSessionAvailable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CurrentUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PageTitle,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? OperatorUserId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OperatorDisplayName);

public sealed record SnapshotOption(
    string Value,
    string Label,
    bool Selected,
    bool Disabled);

public sealed record SnapshotElement(
    string Ref,
    string Role,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type,
    bool Disabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Checked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<SnapshotOption>? Options = null);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Status,
    string Kind);

public sealed record NavigateRequest(string Url);
public sealed record ClickRequest(string Ref);
public sealed record FillRequest(string Ref, string Value);
public sealed record PressRequest(string? Ref, string Key);
public sealed record SelectOptionRequest(string Ref, string? Value, string? Label);
public sealed record SetCheckedRequest(string Ref, bool Checked);

public interface IBrowserOperations
{
    Task<BrowserStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<BrowserActionResult> NavigateAsync(string target, CancellationToken cancellationToken = default);
    Task<BrowserSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
    Task<BrowserActionResult> ClickAsync(string elementRef, CancellationToken cancellationToken = default);
    Task<BrowserActionResult> FillAsync(string elementRef, string value, CancellationToken cancellationToken = default);
    Task<BrowserActionResult> PressAsync(string? elementRef, string key, CancellationToken cancellationToken = default);
    Task<BrowserActionResult> SelectOptionAsync(
        string elementRef,
        string? value,
        string? label,
        CancellationToken cancellationToken = default);
    Task<BrowserActionResult> SetCheckedAsync(
        string elementRef,
        bool isChecked,
        CancellationToken cancellationToken = default);
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
