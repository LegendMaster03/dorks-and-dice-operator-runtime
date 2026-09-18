using System.Net.Http.Headers;
using System.Net.Http.Json;
using DorksAndDice.OperatorRuntime.Configuration;

namespace DorksAndDice.OperatorRuntime.Site;

public sealed class OperatorBootstrapClient(
    HttpClient httpClient,
    OperatorRuntimeOptions options,
    ILogger<OperatorBootstrapClient> logger)
{
    public async Task<OperatorIdentity> GetCurrentOperatorAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateOperatorRequest(HttpMethod.Get, "/operator/v1/me");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Operator API /operator/v1/me rejected the configured credential with HTTP {(int)response.StatusCode}.");
        }

        var identity = await response.Content.ReadFromJsonAsync<OperatorIdentity>(cancellationToken)
            ?? throw new InvalidOperationException("Operator API /operator/v1/me returned an empty response.");

        logger.LogInformation(
            "Verified Site service principal {DisplayName} ({UserId}) with roles {Roles}",
            identity.DisplayName,
            identity.UserId,
            string.Join(",", identity.GlobalRoles));

        return identity;
    }

    public async Task<BrowserBootstrap> CreateBrowserBootstrapAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateOperatorRequest(HttpMethod.Post, "/operator/v1/browser-bootstrap");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Operator API /operator/v1/browser-bootstrap failed with HTTP {(int)response.StatusCode}.");
        }

        var bootstrap = await response.Content.ReadFromJsonAsync<BrowserBootstrap>(cancellationToken)
            ?? throw new InvalidOperationException("Operator bootstrap endpoint returned an empty response.");

        logger.LogInformation(
            "Issued browser bootstrap {BootstrapId} expiring at {ExpiresAt}",
            bootstrap.BootstrapId,
            bootstrap.ExpiresAt);

        return bootstrap;
    }

    private HttpRequestMessage CreateOperatorRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(options.SiteUri, relativePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.OperatorToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
}
