namespace DorksAndDice.OperatorRuntime.Site;

public sealed record OperatorIdentity(
    Guid UserId,
    string DisplayName,
    string AccountKind,
    IReadOnlyList<string> GlobalRoles);

public sealed record BrowserBootstrap(
    Guid BootstrapId,
    string BootstrapUrl,
    DateTimeOffset ExpiresAt);
