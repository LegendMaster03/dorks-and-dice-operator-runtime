using DorksAndDice.OperatorRuntime.Browser;
using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DorksAndDice.OperatorRuntime.Tests;

public sealed class ConfigurationAndSecurityTests
{
    [Fact]
    public void MissingSiteUrlIsRejected()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["DORKS_OPERATOR_TOKEN"] = "operator-secret",
            ["DORKS_RUNTIME_API_TOKEN"] = "runtime-secret"
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => OperatorRuntimeOptions.FromConfiguration(configuration, smokeMode: false));
        Assert.Contains("DORKS_SITE_URL", exception.Message);
    }

    [Fact]
    public void MissingOperatorCredentialIsRejected()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["DORKS_SITE_URL"] = "https://dorks-and-dice.com",
            ["DORKS_RUNTIME_API_TOKEN"] = "runtime-secret"
        });

        var exception = Assert.Throws<OptionsValidationException>(
            () => OperatorRuntimeOptions.FromConfiguration(configuration, smokeMode: false));
        Assert.Contains("DORKS_OPERATOR_TOKEN", exception.Message);
    }

    [Theory]
    [InlineData("/api/v1/browser/status")]
    [InlineData("/mcp")]
    [InlineData("/mcp/")]
    public async Task RuntimeControlSurfacesAreNotAnonymous(string path)
    {
        var options = new OperatorRuntimeOptions
        {
            SiteUri = new Uri("https://dorks-and-dice.com/"),
            OperatorToken = "operator-secret",
            RuntimeApiToken = "separate-runtime-secret"
        };
        var called = false;
        var middleware = new RuntimeApiAuthenticationMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            options);

        var anonymous = new DefaultHttpContext();
        anonymous.Request.Path = path;
        anonymous.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(anonymous);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        Assert.False(called);

        var responseBody = new StreamReader(anonymous.Response.Body);
        anonymous.Response.Body.Position = 0;
        var unauthorizedText = await responseBody.ReadToEndAsync();
        Assert.DoesNotContain(options.RuntimeApiToken!, unauthorizedText, StringComparison.Ordinal);
        Assert.DoesNotContain(options.OperatorToken, unauthorizedText, StringComparison.Ordinal);

        var authorized = new DefaultHttpContext();
        authorized.Request.Path = path;
        authorized.Request.Headers.Authorization = "Bearer separate-runtime-secret";
        authorized.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(authorized);
        Assert.True(called);
    }

    [Fact]
    public void ForeignNavigationOriginIsRejected()
    {
        var options = new OperatorRuntimeOptions
        {
            SiteUri = new Uri("https://dorks-and-dice.com/"),
            OperatorToken = "operator-secret",
            RuntimeApiToken = "runtime-secret"
        };
        var policy = new NavigationPolicy(options);

        Assert.Equal(
            "https://dorks-and-dice.com/tools/rules-core",
            policy.Resolve("/tools/rules-core").AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() => policy.Resolve("https://example.com/"));
        Assert.Throws<InvalidOperationException>(() => policy.Resolve("//example.com/path"));
        Assert.Throws<InvalidOperationException>(() => policy.Resolve("https://dorks-and-dice.com@example.com/"));
    }

    [Fact]
    public async Task OperationGateSerializesConcurrentCommands()
    {
        var gate = new BrowserOperationGate();
        var active = 0;
        var maximum = 0;

        var tasks = Enumerable.Range(0, 12).Select(_ =>
            gate.RunAsync(async () =>
            {
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                await Task.Delay(15);
                Interlocked.Decrement(ref active);
                return true;
            }));

        await Task.WhenAll(tasks);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public void DiagnosticBufferIsBounded()
    {
        var buffer = new BoundedBuffer<int>(100);
        for (var i = 0; i < 150; i++)
        {
            buffer.Add(i);
        }

        var snapshot = buffer.Snapshot();
        Assert.Equal(100, snapshot.Count);
        Assert.Equal(50, snapshot[0]);
        Assert.Equal(149, snapshot[^1]);
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
