using System.Reactive.Concurrency;
using ReactiveUI;

namespace CableRoutePlanner.Tests;

// Plain ViewModel tests must not inherit an Avalonia dispatcher left by an earlier
// UI test. Test parallelization is disabled; restore the scheduler after each test.
public abstract class ReactiveTestScope : IDisposable
{
    private readonly IScheduler _previous = RxApp.MainThreadScheduler;

    protected ReactiveTestScope() => RxApp.MainThreadScheduler = ImmediateScheduler.Instance;

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            RxApp.MainThreadScheduler = _previous;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
