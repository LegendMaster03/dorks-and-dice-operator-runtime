namespace DorksAndDice.OperatorRuntime.Browser;

internal sealed class BrowserOperationGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _gate.Release();
        }
    }
}
