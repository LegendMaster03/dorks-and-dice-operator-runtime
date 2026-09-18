using Microsoft.Playwright;

namespace DorksAndDice.OperatorRuntime.Browser;

internal sealed class BrowserSession(
    IBrowserContext context,
    IPage page,
    BoundedBuffer<BrowserConsoleEntry> console,
    BoundedBuffer<BrowserNetworkError> networkErrors,
    BrowserNavigationBoundary navigationBoundary)
{
    private readonly Dictionary<string, IElementHandle> _elementReferences = new(StringComparer.Ordinal);
    private long _nextReference;

    public IBrowserContext Context { get; } = context;
    public IPage Page { get; } = page;
    public BoundedBuffer<BrowserConsoleEntry> Console { get; } = console;
    public BoundedBuffer<BrowserNetworkError> NetworkErrors { get; } = networkErrors;
    public BrowserNavigationBoundary NavigationBoundary { get; } = navigationBoundary;

    public void ClearElementReferences() => _elementReferences.Clear();

    public string AddElementReference(IElementHandle element)
    {
        var value = $"e{Interlocked.Increment(ref _nextReference)}";
        _elementReferences[value] = element;
        return value;
    }

    public IElementHandle ResolveElement(string elementRef)
    {
        if (!_elementReferences.TryGetValue(elementRef, out var element))
        {
            throw new KeyNotFoundException(
                $"Element reference '{elementRef}' is unknown or stale. Request a new browser snapshot.");
        }

        return element;
    }
}

internal sealed class BrowserNavigationBoundary(Uri siteUri)
{
    private long _violationVersion;
    private long _navigationActivityVersion;

    public long ViolationVersion => Interlocked.Read(ref _violationVersion);

    public bool HasViolationSince(long version) => ViolationVersion != version;

    public async Task WaitForNavigationQuiescenceAsync(CancellationToken cancellationToken)
    {
        // Popup/document requests may be delivered just after the Playwright
        // action itself resolves. Wait until navigation activity has remained
        // unchanged for one short interval, with a bounded upper limit.
        var observedVersion = Interlocked.Read(ref _navigationActivityVersion);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            var currentVersion = Interlocked.Read(ref _navigationActivityVersion);
            if (currentVersion == observedVersion)
            {
                return;
            }

            observedVersion = currentVersion;
        }
    }

    public async Task InstallRoutingAsync(IBrowserContext context)
    {
        await context.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            if (request.IsNavigationRequest)
            {
                Interlocked.Increment(ref _navigationActivityVersion);
            }

            if (request.IsNavigationRequest
                && await IsTopLevelNavigationAsync(request)
                && !IsSiteOrigin(request.Url))
            {
                RecordViolation();
                await route.AbortAsync("blockedbyclient");
                return;
            }

            await route.ContinueAsync();
        });
    }

    public void StartSinglePageEnforcement(IBrowserContext context, IPage controlledPage)
    {
        context.Page += (_, page) =>
        {
            if (ReferenceEquals(page, controlledPage))
            {
                return;
            }

            RecordViolation();
            _ = ClosePageQuietlyAsync(page);
        };

        controlledPage.FrameNavigated += (_, frame) =>
        {
            if (frame.ParentFrame is not null || IsSiteOrigin(frame.Url))
            {
                return;
            }

            RecordViolation();
            _ = CloseContextQuietlyAsync(context);
        };
    }

    private void RecordViolation() => Interlocked.Increment(ref _violationVersion);

    private bool IsSiteOrigin(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && NavigationPolicy.SameOrigin(siteUri, uri);

    private static async Task<bool> IsTopLevelNavigationAsync(IRequest request)
    {
        try
        {
            return request.Frame.ParentFrame is null;
        }
        catch (PlaywrightException)
        {
            // Playwright documents that a navigation request can be emitted before
            // its frame exists. Chromium still identifies the destination type:
            // top-level pages/popups use "document"; embedded frames use "iframe"
            // or "frame". Treat unknown frame-less navigation conservatively.
            var destination = await request.HeaderValueAsync("sec-fetch-dest");
            return !string.Equals(destination, "iframe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destination, "frame", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task ClosePageQuietlyAsync(IPage page)
    {
        try
        {
            if (!page.IsClosed)
            {
                await page.CloseAsync();
            }
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task CloseContextQuietlyAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }
}
