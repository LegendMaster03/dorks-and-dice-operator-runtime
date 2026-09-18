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

    public long ViolationVersion => Interlocked.Read(ref _violationVersion);

    public bool HasViolationSince(long version) => ViolationVersion != version;

    public async Task InstallRoutingAsync(IBrowserContext context)
    {
        await context.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            if (request.IsNavigationRequest
                && IsTopLevelNavigation(request, context)
                && !IsSiteOrigin(request.Url))
            {
                RecordViolation();
                await route.AbortAsync();
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

    private static bool IsTopLevelNavigation(IRequest request, IBrowserContext context)
    {
        try
        {
            return request.Frame.ParentFrame is null;
        }
        catch (PlaywrightException)
        {
            // A popup navigation can be issued before Playwright exposes its frame.
            // A second page is a reliable signal that this is not a subframe request.
            return context.Pages.Count > 1;
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
