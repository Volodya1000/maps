using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using CableRoutePlanner.Application;
using CableRoutePlanner.ViewModels;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class StartupTests : ReactiveTestScope
{
    [Fact]
    public async Task FailedStartupShowsErrorAndRetryOpensTheApplication()
    {
        var screen = new NavigationHost();
        using var map = new MapViewModel(screen, new PlanningSession(TestData.Planner(), TestData.AllowAllValidator),
            new MemoryData(), new MemorySettings(), new MemoryClipboard());
        using var settings = new SettingsViewModel(screen, new MemorySettings());
        using var shell = new ShellViewModel(screen, map, settings);
        var failedOnce = false;
        using var startup = new StartupViewModel((_, _) =>
        {
            if (!failedOnce)
            {
                failedOnce = true;
                throw new IOException("offline package unavailable");
            }
            return Task.FromResult(shell);
        });

        await startup.Load.Execute();
        Assert.Null(startup.Shell);
        Assert.Contains("offline package unavailable", startup.Message);
        Assert.False(startup.IsLoading);
        Assert.True(await startup.Load.CanExecute.FirstAsync());

        await startup.Load.Execute();
        Assert.Same(shell, startup.Shell);
        Assert.False(startup.IsLoading);
    }

    [Fact]
    public async Task CancelledStartupDoesNotOpenAStaleScreen()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new StartupViewModel(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Cancelled preparation must not reach screen creation.");
        });
        var load = startup.Load.Execute().ToTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            startup.Dispose();
        }
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(startup.Shell);
        Assert.False(startup.IsLoading);
        Assert.Contains("отменена", startup.Message);
    }
}
