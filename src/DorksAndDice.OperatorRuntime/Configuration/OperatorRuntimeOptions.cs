using Microsoft.Extensions.Options;

namespace DorksAndDice.OperatorRuntime.Configuration;

public sealed class OperatorRuntimeOptions
{
    public required Uri SiteUri { get; init; }
    public required string OperatorToken { get; init; }
    public string? RuntimeApiToken { get; init; }
    public bool SmokeMode { get; init; }
    public bool Headless { get; init; } = true;
    public TimeSpan BrowserTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static OperatorRuntimeOptions FromConfiguration(IConfiguration configuration, bool smokeMode)
    {
        var siteValue = configuration["DORKS_SITE_URL"];
        if (string.IsNullOrWhiteSpace(siteValue))
        {
            throw new OptionsValidationException(
                nameof(OperatorRuntimeOptions),
                typeof(OperatorRuntimeOptions),
                ["DORKS_SITE_URL is required."]);
        }

        if (!Uri.TryCreate(siteValue, UriKind.Absolute, out var siteUri)
            || (siteUri.Scheme != Uri.UriSchemeHttps && siteUri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(siteUri.UserInfo)
            || !string.IsNullOrEmpty(siteUri.Query)
            || !string.IsNullOrEmpty(siteUri.Fragment))
        {
            throw new OptionsValidationException(
                nameof(OperatorRuntimeOptions),
                typeof(OperatorRuntimeOptions),
                ["DORKS_SITE_URL must be an absolute HTTP(S) Site origin without credentials, query, or fragment."]);
        }

        if (siteUri.Scheme == Uri.UriSchemeHttp && !siteUri.IsLoopback)
        {
            throw new OptionsValidationException(
                nameof(OperatorRuntimeOptions),
                typeof(OperatorRuntimeOptions),
                ["DORKS_SITE_URL must use HTTPS except for loopback development/test Sites."]);
        }

        var operatorToken = configuration["DORKS_OPERATOR_TOKEN"];
        if (string.IsNullOrWhiteSpace(operatorToken))
        {
            throw new OptionsValidationException(
                nameof(OperatorRuntimeOptions),
                typeof(OperatorRuntimeOptions),
                ["DORKS_OPERATOR_TOKEN is required."]);
        }

        var runtimeApiToken = configuration["DORKS_RUNTIME_API_TOKEN"];
        if (!smokeMode && string.IsNullOrWhiteSpace(runtimeApiToken))
        {
            throw new OptionsValidationException(
                nameof(OperatorRuntimeOptions),
                typeof(OperatorRuntimeOptions),
                ["DORKS_RUNTIME_API_TOKEN is required when running the service."]);
        }

        var headless = !bool.TryParse(configuration["DORKS_BROWSER_HEADLESS"], out var configuredHeadless)
            || configuredHeadless;

        var timeout = TimeSpan.FromSeconds(30);
        if (int.TryParse(configuration["DORKS_BROWSER_TIMEOUT_SECONDS"], out var timeoutSeconds)
            && timeoutSeconds > 0)
        {
            timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        return new OperatorRuntimeOptions
        {
            SiteUri = new Uri(siteUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute),
            OperatorToken = operatorToken,
            RuntimeApiToken = runtimeApiToken,
            SmokeMode = smokeMode,
            Headless = headless,
            BrowserTimeout = timeout
        };
    }
}
