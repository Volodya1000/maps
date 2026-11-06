using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using CableRoutePlanner.Domain;
using ReactiveUI;

namespace CableRoutePlanner.ViewModels;

public sealed class RouteVerticesTreeViewModel : ReactiveObject, IDisposable
{
    private readonly HashSet<int> _expandedLegs = [];
    private readonly CompositeDisposable _lifetime = new();
    private RouteVertexRow? _selectedRouteVertex;
    public RouteVerticesTreeViewModel()
    {
        ToggleRouteLeg = ReactiveCommand.Create<int>(ToggleLeg).DisposeWith(_lifetime);
        SelectRouteVertex = ReactiveCommand.Create<int>(SelectVertex).DisposeWith(_lifetime);
    }

    public ObservableCollection<RouteVertexRow> RouteVertices { get; } = [];

    public ObservableCollection<RouteVertexRow> VisibleRouteVertices { get; } = [];

    public ReactiveCommand<int, Unit> ToggleRouteLeg { get; }

    public ReactiveCommand<int, Unit> SelectRouteVertex { get; }

    public RouteVertexRow? SelectedRouteVertex
    {
        get => _selectedRouteVertex;
        set
        {
            RouteVertexRow? selected = RowSelection.Apply(
                RouteVertices,
                value,
                row => row.Number,
                row => row.IsSelected,
                (row, isSelected) => row with { IsSelected = isSelected });
            this.RaiseAndSetIfChanged(ref _selectedRouteVertex, selected);
            if (selected is { IsChild: true })
            {
                _expandedLegs.Add(selected.LegIndex);
            }

            RefreshVisibleVertices();
            this.RaisePropertyChanged(nameof(CopyRouteVertexText));
        }
    }

    public string CopyRouteVertexText => SelectedRouteVertex is { } vertex
        ? $"Копировать вершину {vertex.DisplayLabel}"
        : "Выберите вершину";

    public void SetVertices(IEnumerable<RouteVertexRow> vertices)
    {
        RouteVertices.Clear();
        foreach (RouteVertexRow vertex in vertices)
        {
            RouteVertices.Add(vertex);
        }

        _expandedLegs.Clear();
        bool hadSelection = _selectedRouteVertex is not null;
        _selectedRouteVertex = null;
        if (hadSelection)
        {
            this.RaisePropertyChanged(nameof(SelectedRouteVertex));
            this.RaisePropertyChanged(nameof(CopyRouteVertexText));
        }
        RefreshVisibleVertices();
    }

    private void ToggleLeg(int leg)
    {
        if (!_expandedLegs.Add(leg))
        {
            _expandedLegs.Remove(leg);
        }

        RefreshVisibleVertices();
    }

    private void SelectVertex(int number) =>
        SelectedRouteVertex = RouteVertices.FirstOrDefault(vertex => vertex.Number == number);

    private void RefreshVisibleVertices()
    {
        VisibleRouteVertices.Clear();
        HashSet<int> legsWithChildren = RouteVertices
            .Where(vertex => vertex.IsChild)
            .Select(vertex => vertex.LegIndex)
            .ToHashSet();
        foreach (RouteVertexRow vertex in RouteVertices)
        {
            if (!vertex.IsChild || _expandedLegs.Contains(vertex.LegIndex))
            {
                VisibleRouteVertices.Add(vertex with
                {
                    HasChildren = !vertex.IsChild && legsWithChildren.Contains(vertex.LegIndex),
                    IsExpanded = _expandedLegs.Contains(vertex.LegIndex)
                });
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Dispose();
    }
}
