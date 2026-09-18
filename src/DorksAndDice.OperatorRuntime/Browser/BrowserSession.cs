using Microsoft.Playwright;

namespace DorksAndDice.OperatorRuntime.Browser;

internal sealed class BrowserSession(
    IBrowserContext context,
    IPage page,
    BoundedBuffer<BrowserConsoleEntry> console,
    BoundedBuffer<BrowserNetworkError> networkErrors)
{
    private readonly Dictionary<string, IElementHandle> _elementReferences = new(StringComparer.Ordinal);
    private long _nextReference;

    public IBrowserContext Context { get; } = context;
    public IPage Page { get; } = page;
    public BoundedBuffer<BrowserConsoleEntry> Console { get; } = console;
    public BoundedBuffer<BrowserNetworkError> NetworkErrors { get; } = networkErrors;

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
