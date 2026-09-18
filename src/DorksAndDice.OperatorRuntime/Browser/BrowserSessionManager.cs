using DorksAndDice.OperatorRuntime.Configuration;
using DorksAndDice.OperatorRuntime.Site;
using Microsoft.Playwright;

namespace DorksAndDice.OperatorRuntime.Browser;

public sealed class BrowserSessionManager(
    OperatorRuntimeOptions options,
    OperatorBootstrapClient operatorClient,
    NavigationPolicy navigationPolicy,
    ILogger<BrowserSessionManager> logger) : IBrowserOperations, IAsyncDisposable
{
    private const int DiagnosticBufferCapacity = 100;
    private const int SnapshotTextLimit = 20_000;
    private const int ElementNameLimit = 500;

    private readonly BrowserOperationGate _operationGate = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private BrowserSession? _session;
    private OperatorIdentity? _identity;
    private bool _disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _identity = await operatorClient.GetCurrentOperatorAsync(cancellationToken);

        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Headless
            });
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Chromium could not initialize for the Operator Runtime.", exception);
        }

        await RecoverSessionCoreAsync(cancellationToken);
        logger.LogInformation(
            "Operator Runtime browser initialized for Site origin {SiteOrigin}",
            options.SiteUri.GetLeftPart(UriPartial.Authority));
    }

    public async Task<BrowserStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _session;
        var title = session is { Page.IsClosed: false }
            ? await SafeTitleAsync(session.Page)
            : null;

        return new BrowserStatus(
            _playwright is not null,
            _browser?.IsConnected == true,
            IsSessionUsable(session),
            session is { Page.IsClosed: false } ? SanitizeUrl(session.Page.Url) : null,
            title,
            _identity?.UserId,
            _identity?.DisplayName);
    }

    public Task<BrowserActionResult> NavigateAsync(string target, CancellationToken cancellationToken = default) =>
        ExecutePageOperationAsync(
            async session =>
            {
                var targetUri = navigationPolicy.Resolve(target);
                logger.LogInformation("Browser navigate to {Path}", targetUri.AbsolutePath);
                var response = await session.Page.GotoAsync(targetUri.AbsoluteUri, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = (float)options.BrowserTimeout.TotalMilliseconds
                });

                session.ClearElementReferences();
                if (response is null)
                {
                    throw new InvalidOperationException("Navigation did not produce a response.");
                }

                ThrowIfAuthenticationLost(session);
                return await ActionResultAsync(session, "Navigation completed.");
            },
            cancellationToken);

    public Task<BrowserSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            var session = await EnsureSessionBeforeOperationAsync(cancellationToken);
            session.ClearElementReferences();

            var headings = (await session.Page.Locator("h1,h2,h3,h4,h5,h6").AllInnerTextsAsync())
                .Select(NormalizeText)
                .Where(value => value.Length > 0)
                .Take(100)
                .ToArray();

            var bodyText = NormalizeText(await session.Page.Locator("body").InnerTextAsync());
            if (bodyText.Length > SnapshotTextLimit)
            {
                bodyText = bodyText[..SnapshotTextLimit] + " …";
            }

            var elements = new List<SnapshotElement>();
            var handles = await session.Page
                .Locator("a,button,input,textarea,select,[role],[contenteditable='true']")
                .ElementHandlesAsync();

            foreach (var handle in handles.Take(500))
            {
                var role = await GetRoleAsync(handle);
                var name = await GetElementNameAsync(handle);
                var type = await handle.GetAttributeAsync("type");
                var disabled = false;
                try
                {
                    disabled = await handle.IsDisabledAsync();
                }
                catch (PlaywrightException)
                {
                    // Some explicit-role elements do not implement disabled state.
                }

                elements.Add(new SnapshotElement(
                    session.AddElementReference(handle),
                    role,
                    name,
                    type,
                    disabled));
            }

            return new BrowserSnapshot(
                SanitizeUrl(session.Page.Url) ?? options.SiteUri.AbsoluteUri,
                await session.Page.TitleAsync(),
                headings,
                bodyText,
                elements);
        }, cancellationToken);

    public Task<BrowserActionResult> ClickAsync(string elementRef, CancellationToken cancellationToken = default) =>
        ExecutePageOperationAsync(
            async session =>
            {
                var element = session.ResolveElement(elementRef);
                await element.ClickAsync(new ElementHandleClickOptions
                {
                    Timeout = (float)options.BrowserTimeout.TotalMilliseconds
                });
                session.ClearElementReferences();
                await WaitForPageSettleAsync(session.Page);
                ThrowIfAuthenticationLost(session);
                return await ActionResultAsync(session, "Click completed.");
            },
            cancellationToken);

    public Task<BrowserActionResult> FillAsync(string elementRef, string value, CancellationToken cancellationToken = default) =>
        ExecutePageOperationAsync(
            async session =>
            {
                var element = session.ResolveElement(elementRef);
                await element.FillAsync(value, new ElementHandleFillOptions
                {
                    Timeout = (float)options.BrowserTimeout.TotalMilliseconds
                });
                return await ActionResultAsync(session, "Fill completed.");
            },
            cancellationToken);

    public Task<BrowserActionResult> PressAsync(string? elementRef, string key, CancellationToken cancellationToken = default) =>
        ExecutePageOperationAsync(
            async session =>
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("A keyboard key is required.", nameof(key));
                }

                if (string.IsNullOrWhiteSpace(elementRef))
                {
                    await session.Page.Keyboard.PressAsync(key);
                }
                else
                {
                    await session.ResolveElement(elementRef).PressAsync(key);
                }

                session.ClearElementReferences();
                await WaitForPageSettleAsync(session.Page);
                ThrowIfAuthenticationLost(session);
                return await ActionResultAsync(session, "Key press completed.");
            },
            cancellationToken);

    public Task<BrowserScreenshot> ScreenshotAsync(CancellationToken cancellationToken = default) =>
        _operationGate.RunAsync(async () =>
        {
            var session = await EnsureSessionBeforeOperationAsync(cancellationToken);
            var bytes = await session.Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                FullPage = false
            });

            return new BrowserScreenshot("image/png", Convert.ToBase64String(bytes), bytes.Length);
        }, cancellationToken);

    public IReadOnlyList<BrowserConsoleEntry> GetConsole() =>
        _session?.Console.Snapshot() ?? [];

    public IReadOnlyList<BrowserNetworkError> GetNetworkErrors() =>
        _session?.NetworkErrors.Snapshot() ?? [];

    internal async Task ForceContextLossForTestAsync()
    {
        if (_session is not null)
        {
            await _session.Context.CloseAsync();
        }
    }

    private Task<T> ExecutePageOperationAsync<T>(
        Func<BrowserSession, Task<T>> operation,
        CancellationToken cancellationToken) =>
        _operationGate.RunAsync(async () =>
        {
            var session = await EnsureSessionBeforeOperationAsync(cancellationToken);
            try
            {
                return await operation(session);
            }
            catch (BrowserAuthenticationLostException exception)
            {
                var recovered = await TryRecoverAfterFailureAsync(cancellationToken);
                throw new BrowserOperationException(
                    recovered
                        ? "The Site session was rejected. A fresh session was established, but the original operation was not replayed."
                        : "The Site session was rejected and recovery failed. The original operation was not replayed.",
                    recovered,
                    exception);
            }
            catch (Exception exception) when (LooksLikeSessionLoss(exception, session))
            {
                var recovered = await TryRecoverAfterFailureAsync(cancellationToken);
                throw new BrowserOperationException(
                    recovered
                        ? "The browser session was lost during the operation. A fresh session was established, but the original operation was not replayed."
                        : "The browser session was lost during the operation and recovery failed. The original operation was not replayed.",
                    recovered,
                    exception);
            }
        }, cancellationToken);

    private async Task<BrowserSession> EnsureSessionBeforeOperationAsync(CancellationToken cancellationToken)
    {
        if (IsSessionUsable(_session))
        {
            return _session!;
        }

        await RecoverSessionCoreAsync(cancellationToken);
        return _session ?? throw new InvalidOperationException("Browser session recovery did not create a session.");
    }

    private async Task<bool> TryRecoverAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RecoverSessionCoreAsync(cancellationToken);
            return true;
        }
        catch (Exception recoveryException)
        {
            logger.LogWarning(
                "Browser session recovery failed with {FailureType}",
                recoveryException.GetType().Name);
            return false;
        }
    }

    private async Task RecoverSessionCoreAsync(CancellationToken cancellationToken)
    {
        if (_browser?.IsConnected != true)
        {
            if (_playwright is null)
            {
                _playwright = await Playwright.CreateAsync();
            }

            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = options.Headless
            });
        }

        if (_session is not null)
        {
            try
            {
                await _session.Context.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // The old context is already gone.
            }

            _session = null;
        }

        var bootstrap = await operatorClient.CreateBrowserBootstrapAsync(cancellationToken);
        var bootstrapUri = new Uri(options.SiteUri, bootstrap.BootstrapUrl);
        if (!NavigationPolicy.SameOrigin(options.SiteUri, bootstrapUri))
        {
            throw new InvalidOperationException("Site returned a browser bootstrap URL outside the configured Site origin.");
        }

        var context = await _browser.NewContextAsync();
        var console = new BoundedBuffer<BrowserConsoleEntry>(DiagnosticBufferCapacity);
        var networkErrors = new BoundedBuffer<BrowserNetworkError>(DiagnosticBufferCapacity);
        var page = await context.NewPageAsync();
        AttachDiagnostics(page, console, networkErrors);

        try
        {
            IResponse? response;
            try
            {
                response = await page.GotoAsync(bootstrapUri.AbsoluteUri, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = (float)options.BrowserTimeout.TotalMilliseconds
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Browser bootstrap navigation failed with {FailureType}",
                    exception.GetType().Name);
                throw new InvalidOperationException("Browser bootstrap navigation failed.");
            }

            if (response is null)
            {
                throw new InvalidOperationException("Browser bootstrap navigation returned no response.");
            }

            if (new Uri(page.Url).AbsolutePath.StartsWith("/operator/bootstrap", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Browser bootstrap did not redirect into a normal Site session.");
            }

            await VerifyAuthenticatedBrowserAsync(context, cancellationToken);
            _session = new BrowserSession(context, page, console, networkErrors);
            logger.LogInformation(
                "Authenticated Chromium Site session established from bootstrap {BootstrapId}",
                bootstrap.BootstrapId);
        }
        catch
        {
            try
            {
                await context.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // Preserve the already-sanitized bootstrap/session failure.
            }

            throw;
        }
    }

    private async Task VerifyAuthenticatedBrowserAsync(IBrowserContext context, CancellationToken cancellationToken)
    {
        var verificationPage = await context.NewPageAsync();
        try
        {
            var response = await verificationPage.GotoAsync(new Uri(options.SiteUri, "/account").AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = (float)options.BrowserTimeout.TotalMilliseconds
            });

            var finalUri = new Uri(verificationPage.Url);
            var authenticated = response?.Status == StatusCodes.Status200OK
                && NavigationPolicy.SameOrigin(options.SiteUri, finalUri)
                && string.Equals(finalUri.AbsolutePath.TrimEnd('/'), "/account", StringComparison.OrdinalIgnoreCase);

            if (!authenticated)
            {
                throw new InvalidOperationException(
                    "Browser bootstrap did not establish an authenticated normal Site session.");
            }

            var cookies = await context.CookiesAsync([options.SiteUri.AbsoluteUri]);
            if (cookies.Count == 0)
            {
                throw new InvalidOperationException(
                    "Authenticated Site verification succeeded without a browser cookie.");
            }
        }
        finally
        {
            await verificationPage.CloseAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void AttachDiagnostics(
        IPage page,
        BoundedBuffer<BrowserConsoleEntry> console,
        BoundedBuffer<BrowserNetworkError> networkErrors)
    {
        page.Console += (_, message) =>
        {
            console.Add(new BrowserConsoleEntry(
                DateTimeOffset.UtcNow,
                message.Type,
                Truncate(message.Text, 2_000)));
        };

        page.RequestFailed += (_, request) =>
        {
            networkErrors.Add(new BrowserNetworkError(
                DateTimeOffset.UtcNow,
                request.Method,
                SanitizeUrl(request.Url) ?? options.SiteUri.AbsoluteUri,
                null,
                "request-failed"));
        };

        page.Response += (_, response) =>
        {
            if (response.Status < 400)
            {
                return;
            }

            networkErrors.Add(new BrowserNetworkError(
                DateTimeOffset.UtcNow,
                response.Request.Method,
                SanitizeUrl(response.Url) ?? options.SiteUri.AbsoluteUri,
                response.Status,
                "http-error"));
        };
    }

    private static async Task<string> GetRoleAsync(IElementHandle element)
    {
        var explicitRole = await element.GetAttributeAsync("role");
        if (!string.IsNullOrWhiteSpace(explicitRole))
        {
            return explicitRole;
        }

        var tagName = await element.EvaluateAsync<string>("element => element.tagName.toLowerCase()");
        return tagName switch
        {
            "a" => "link",
            "button" => "button",
            "textarea" => "textbox",
            "select" => "combobox",
            "input" => InputRole(await element.GetAttributeAsync("type")),
            _ => tagName
        };
    }

    private static string InputRole(string? type) =>
        type?.ToLowerInvariant() switch
        {
            "checkbox" => "checkbox",
            "radio" => "radio",
            "button" or "submit" or "reset" => "button",
            _ => "textbox"
        };

    private static async Task<string?> GetElementNameAsync(IElementHandle element)
    {
        foreach (var attribute in new[] { "aria-label", "name", "placeholder", "title", "alt" })
        {
            var value = NormalizeText(await element.GetAttributeAsync(attribute) ?? string.Empty);
            if (value.Length > 0)
            {
                return Truncate(value, ElementNameLimit);
            }
        }

        var text = NormalizeText(await element.InnerTextAsync());
        return text.Length == 0 ? null : Truncate(text, ElementNameLimit);
    }

    private async Task WaitForPageSettleAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
            {
                Timeout = (float)Math.Min(options.BrowserTimeout.TotalMilliseconds, 3_000)
            });
        }
        catch (PlaywrightException)
        {
            if (page.IsClosed)
            {
                throw;
            }
        }
    }

    private static void ThrowIfAuthenticationLost(BrowserSession session)
    {
        if (Uri.TryCreate(session.Page.Url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.StartsWith("/account/login", StringComparison.OrdinalIgnoreCase))
        {
            throw new BrowserAuthenticationLostException();
        }
    }

    private static bool IsSessionUsable(BrowserSession? session) =>
        session is not null && !session.Page.IsClosed;

    private bool LooksLikeSessionLoss(Exception exception, BrowserSession session)
    {
        if (_browser?.IsConnected != true || session.Page.IsClosed)
        {
            return true;
        }

        return exception is PlaywrightException
            && (exception.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("Target page", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("browser has been closed", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<BrowserActionResult> ActionResultAsync(BrowserSession session, string message) =>
        new(
            true,
            SanitizeUrl(session.Page.Url) ?? session.Page.Url,
            await session.Page.TitleAsync(),
            message);

    private static async Task<string?> SafeTitleAsync(IPage page)
    {
        try
        {
            return await page.TitleAsync();
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static string NormalizeText(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + " …";

    private static string? SanitizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        return uri.GetLeftPart(UriPartial.Path);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session is not null)
        {
            try
            {
                await _session.Context.CloseAsync();
            }
            catch (PlaywrightException)
            {
            }
        }

        if (_browser is not null)
        {
            try
            {
                await _browser.CloseAsync();
            }
            catch (PlaywrightException)
            {
            }
        }

        _playwright?.Dispose();
    }
}
