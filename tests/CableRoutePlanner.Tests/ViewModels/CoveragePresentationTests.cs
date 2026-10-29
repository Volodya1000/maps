using CableRoutePlanner.Application;
using CableRoutePlanner.ViewModels;
using System.Reactive.Linq;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CoveragePresentationTests : ReactiveTestScope
{
    [Fact]
    public async Task AddPointCommandExplainsMissingCoverageAndKeepsExistingPoints()
    {
        MemoryData repository = CoverageTestData.Repository();
        var session = new PlanningSession(CoverageTestData.Planner(repository), TestData.AllowAllValidator);
        using MapViewModel vm = TestData.CreateMapViewModel(new NavigationHost(), session,
            new MapAreaLoader(repository), new MemorySettings(), new MemoryClipboard());
        await vm.AddPoint.Execute(TestData.P(50, 500));
        long revision = session.Revision;

        await vm.AddPoint.Execute(TestData.P(175, 500));

        Assert.Single(vm.Points);
        Assert.Equal(revision, session.Revision);
        Assert.Equal("Для этой точки нет данных в офлайн-пакете.", vm.Status);
    }
}
