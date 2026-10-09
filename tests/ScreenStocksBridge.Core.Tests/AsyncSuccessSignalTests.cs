using ScreenStocksBridge;
using Xunit;

namespace ScreenStocksBridge.Core.Tests;

public sealed class AsyncSuccessSignalTests
{
    [Fact]
    public void SuccessfulFetchCanBeConsumedOnceOnTheMainThread()
    {
        var signal = new AsyncSuccessSignal();

        signal.Observe(Task.FromResult(true));

        Assert.True(signal.TryConsume());
        Assert.False(signal.TryConsume());
    }

    [Fact]
    public void FailedFetchDoesNotRequestARefresh()
    {
        var signal = new AsyncSuccessSignal();

        signal.Observe(Task.FromResult(false));

        Assert.False(signal.TryConsume());
    }
}
