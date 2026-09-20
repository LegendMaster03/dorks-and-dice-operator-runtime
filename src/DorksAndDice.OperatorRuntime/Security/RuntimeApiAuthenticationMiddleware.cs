using System.Security.Cryptography;
using System.Text;
using DorksAndDice.OperatorRuntime.Configuration;

namespace DorksAndDice.OperatorRuntime.Security;

public sealed class RuntimeApiAuthenticationMiddleware(
    RequestDelegate next,
    OperatorRuntimeOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresRuntimeAuthentication(context.Request.Path))
        {
            await next(context);
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !FixedTimeEquals(authorization[prefix.Length..], options.RuntimeApiToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized runtime client." });
            return;
        }

        await next(context);
    }

    private static bool RequiresRuntimeAuthentication(PathString path) =>
        path.StartsWithSegments("/api/v1", StringComparison.Ordinal)
        || path.StartsWithSegments("/mcp", StringComparison.Ordinal);

    private static bool FixedTimeEquals(string supplied, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }
}
