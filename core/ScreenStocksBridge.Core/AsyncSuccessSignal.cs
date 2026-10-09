using System.Threading;
using System.Threading.Tasks;

namespace ScreenStocksBridge;

internal sealed class AsyncSuccessSignal
{
    private int _pending;

    internal void Observe(Task<bool> task)
    {
        task.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion && completed.Result)
                    Interlocked.Exchange(ref _pending, 1);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal bool TryConsume() => Interlocked.Exchange(ref _pending, 0) != 0;
}
