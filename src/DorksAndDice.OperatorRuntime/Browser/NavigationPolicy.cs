using DorksAndDice.OperatorRuntime.Configuration;

namespace DorksAndDice.OperatorRuntime.Browser;

public sealed class NavigationPolicy(OperatorRuntimeOptions options)
{
    public Uri Resolve(string target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || !Uri.TryCreate(options.SiteUri, target, out var resolved)
            || resolved is null)
        {
            throw new ArgumentException("Navigation target is not a valid URL or Site-relative path.", nameof(target));
        }

        if (!string.IsNullOrEmpty(resolved.UserInfo)
            || !SameOrigin(options.SiteUri, resolved))
        {
            throw new InvalidOperationException("Navigation outside the configured Dorks & Dice Site origin is not allowed.");
        }

        return resolved;
    }

    public static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
}
