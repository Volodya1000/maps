using CableRoutePlanner.Domain;
using CableRoutePlanner.ViewModels;
using System.Collections.Specialized;
using System.Reactive.Threading.Tasks;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class RouteVerticesTreeViewModelTests
{
    private static readonly RouteVertexRow[] ThreeVertices =
    [
        new(1, TestData.P(0, 0), LegIndex: 1),
        new(2, TestData.P(1, 0), LegIndex: 2),
        new(3, TestData.P(2, 0), LegIndex: 2, LegSubIndex: 1),
    ];

    private static readonly RouteVertexRow[] ChildVertices =
    [
        new(1, TestData.P(0, 0), LegIndex: 1),
        new(2, TestData.P(1, 0), LegIndex: 1, LegSubIndex: 1),
        new(3, TestData.P(2, 0), LegIndex: 2),
    ];

    private static readonly int[] ExpandedNumbers = [1, 2, 3];
    private static readonly int[] CollapsedNumbers = [1, 2];

    [Fact]
    public async Task TogglingLegOnlyChangesVisibleChildrenAndPreservesFullRoute()
    {
        using RouteVerticesTreeViewModel tree = new();
        tree.SetVertices(ThreeVertices);

        await tree.ToggleRouteLeg.Execute(2).ToTask();

        Assert.Equal(ThreeVertices, tree.RouteVertices);
        Assert.Equal(ExpandedNumbers, tree.VisibleRouteVertices.Select(vertex => vertex.Number));
        Assert.True(tree.VisibleRouteVertices.Single(vertex => vertex.Number == 2).IsExpanded);

        await tree.ToggleRouteLeg.Execute(2).ToTask();

        Assert.Equal(CollapsedNumbers, tree.VisibleRouteVertices.Select(vertex => vertex.Number));
    }

    [Fact]
    public async Task SelectingChildExpandsItsLegAndUpdatesOnlyOneSelection()
    {
        using RouteVerticesTreeViewModel tree = new();
        tree.SetVertices(ChildVertices);

        await tree.SelectRouteVertex.Execute(2).ToTask();

        Assert.Equal(2, tree.SelectedRouteVertex?.Number);
        Assert.Single(tree.RouteVertices, vertex => vertex.IsSelected);
        Assert.Contains(tree.VisibleRouteVertices, vertex => vertex.Number == 2);
        Assert.True(tree.VisibleRouteVertices.Single(vertex => vertex.Number == 1).IsExpanded);
    }

    [Fact]
    public void SettingVerticesRefreshesVisibleRowsOnlyOnce()
    {
        using RouteVerticesTreeViewModel tree = new();
        int changes = 0;
        tree.VisibleRouteVertices.CollectionChanged += OnChanged;

        tree.SetVertices(ThreeVertices);

        Assert.Equal(3, changes);

        void OnChanged(object? sender, NotifyCollectionChangedEventArgs args) => changes += 1;
    }
}
