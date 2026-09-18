namespace DorksAndDice.OperatorRuntime.Browser;

internal sealed class BoundedBuffer<T>(int capacity)
{
    private readonly Queue<T> _items = new(capacity);
    private readonly object _sync = new();

    public void Add(T item)
    {
        lock (_sync)
        {
            if (_items.Count >= capacity)
            {
                _items.Dequeue();
            }

            _items.Enqueue(item);
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_sync)
        {
            return _items.ToArray();
        }
    }
}
