using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI.MapAdapters;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Providers;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using System.Collections.Concurrent;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class MapPerformanceTests
{
    private static readonly string[] SettlementNames = ["First", "Second"];
    [AvaloniaFact]
    public async Task RepeatedTerrainReusesStylesWithoutMergingGeometryOrSettlementNames()
    {
        MapFeature[] roads = Enumerable.Range(1, 20).Select(id =>
            TestData.Line(id, TerrainKind.Road, (id, 10), (id, 100))).ToArray();
        MapFeature first = new(21, TerrainKind.Settlement, new WKBWriter().Write(new Point(27.001, 54.001)), "First", null);
        MapFeature second = new(22, TerrainKind.Settlement, new WKBWriter().Write(new Point(27.002, 54.002)), "Second", null);
        var repository = new MemoryData([.. roads, first, second]);
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, repository.Info.Bounds, repository);
        TileLayer tiled = Assert.Single(control.Map.Layers.OfType<TileLayer>());
        Layer source = Assert.IsType<Layer>(((ISourceLayer)tiled).SourceLayer);
        IProvider provider = Assert.IsAssignableFrom<IProvider>(source.DataSource);
        (double x, double y) = SphericalMercator.FromLonLat(27, 54);

        IFeature[] features = (await provider.GetFeaturesAsync(new FetchInfo(new MSection(new MRect(x, y, x + 1000, y + 1000), 4)))).ToArray();

        IFeature[] renderedRoads = features.Skip(1).Take(20).ToArray();
        Assert.All(renderedRoads, road => Assert.Same(Assert.Single(renderedRoads[0].Styles), Assert.Single(road.Styles)));
        Assert.Equal(20, renderedRoads.Select(road => road.Extent!.MinX).Distinct().Count());
        Assert.Equal(SettlementNames, features.Skip(21).Select(feature => Assert.Single(feature.Styles.OfType<LabelStyle>()).GetLabelText(feature)));
    }

    [AvaloniaFact]
    public async Task ReturningToRecentViewportUsesCachedTiles()
    {
        var repository = new ObservedRepository();
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, repository.Info.Bounds, repository);
        TileLayer layer = Assert.Single(control.Map.Layers.OfType<TileLayer>());
        double resolution = layer.TileSource.Schema.Resolutions[10].UnitsPerPixel;
        (double x, double y) = SphericalMercator.FromLonLat(27, 54);
        double span = 256 * resolution;
        var first = new MRect(x, y, x + span, y + span);
        for (int index = 0; index < 9; index++)
        {
            var area = new MRect(x + index * span * 4, y, x + index * span * 4 + span, y + span);
            await FetchAsync(layer, area, resolution);
            Assert.NotEmpty(layer.GetFeatures(area, resolution));
        }
        repository.UiQueries.Clear();

        await FetchAsync(layer, first, resolution);

        Assert.Empty(repository.UiQueries);
        Assert.NotEmpty(layer.GetFeatures(first, resolution));
    }

    [AvaloniaFact]
    public async Task RasterPreparationRunsOffUiThreadWithAtMostTwoJobs()
    {
        var repository = new ObservedRepository(block: true);
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, repository.Info.Bounds, repository);
        TileLayer layer = Assert.Single(control.Map.Layers.OfType<TileLayer>());
        Task<byte[]?>[] jobs = StartTiles(layer);
        try
        {
            await repository.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            repository.Release.TrySetResult();
        }
        byte[]?[] images = await Task.WhenAll(jobs);

        Assert.Equal(2, repository.MaximumActive);
        Assert.All(repository.UiQueries, onUi => Assert.False(onUi));
        Assert.All(images, bytes => Assert.NotEmpty(bytes!));
    }

    [AvaloniaFact]
    public async Task DisposingMapCancelsQueuedTilesAndLetsRunningTilesFinish()
    {
        var repository = new ObservedRepository(block: true);
        var control = new Mapsui.UI.Avalonia.MapControl();
        var adapter = new MapsuiMapAdapter(control, repository.Info.Bounds, repository);
        bool disposed = false;
        TileLayer layer = Assert.Single(control.Map.Layers.OfType<TileLayer>());
        Task<byte[]?>[] jobs = StartTiles(layer);
        try
        {
            await repository.TwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposed = true;
            adapter.Dispose();
        }
        finally
        {
            repository.Release.TrySetResult();
            if (!disposed)
            {
                adapter.Dispose();
            }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(jobs));
        Assert.Equal(4, jobs.Count(job => job.IsCanceled));
        Assert.Equal(2, jobs.Count(job => job.IsCompletedSuccessfully));
        BruTile.TileInfo tile = Tile(layer, 0);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => layer.TileSource.GetTileAsync(tile));
        adapter.Dispose();
    }

    [AvaloniaFact]
    public async Task FailedRasterJobReleasesCapacityForNextTile()
    {
        var repository = new ObservedRepository(failFirst: true);
        var control = new Mapsui.UI.Avalonia.MapControl();
        using var adapter = new MapsuiMapAdapter(control, repository.Info.Bounds, repository);
        TileLayer layer = Assert.Single(control.Map.Layers.OfType<TileLayer>());
        await Assert.ThrowsAsync<InvalidDataException>(() => layer.TileSource.GetTileAsync(Tile(layer, 0)));

        byte[]? result = await layer.TileSource.GetTileAsync(Tile(layer, 1));

        Assert.NotEmpty(result!);
    }

    private static Task<byte[]?>[] StartTiles(TileLayer layer) => Enumerable.Range(0, 6)
        .Select(index => layer.TileSource.GetTileAsync(Tile(layer, index))).ToArray();

    private static BruTile.TileInfo Tile(TileLayer layer, int offset)
    {
        (double x, double y) = SphericalMercator.FromLonLat(27, 54);
        x += offset * layer.TileSource.Schema.Resolutions[10].UnitsPerPixel * 256;
        return Assert.Single(layer.TileSource.Schema.GetTileInfos(new BruTile.Extent(x, y, x + 1, y + 1), 10));
    }

    private static async Task FetchAsync(TileLayer layer, MRect extent, double resolution)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(layer.Busy) && !layer.Busy)
            {
                done.TrySetResult();
            }
        }
        layer.PropertyChanged += Changed;
        try
        {
            layer.RefreshData(new FetchInfo(new MSection(extent, resolution)));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            layer.PropertyChanged -= Changed;
        }
    }

    private sealed class ObservedRepository(bool block = false, bool failFirst = false) : IGeodataRepository
    {
        private int _active;
        private int _failuresRemaining = failFirst ? 1 : 0;
        private readonly object _gate = new();
        public int MaximumActive { get; private set; }
        public ConcurrentQueue<bool> UiQueries { get; } = new();
        public TaskCompletionSource TwoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DataPackageInfo Info { get; } = new MemoryData().Info;
        public Task<IReadOnlyList<MapFeature>> QueryAsync(GeoBounds bounds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MapFeature>>([]);

        public async Task<IReadOnlyList<MapFeature>> QueryMapAsync(GeoBounds bounds, double metresPerPixel, CancellationToken cancellationToken)
        {
            UiQueries.Enqueue(Dispatcher.UIThread.CheckAccess());
            lock (_gate)
            {
                MaximumActive = Math.Max(MaximumActive, ++_active);
                if (_active >= 2)
                {
                    TwoStarted.TrySetResult();
                }
            }
            try
            {
                if (Interlocked.Exchange(ref _failuresRemaining, 0) > 0)
                {
                    throw new InvalidDataException();
                }
                if (block)
                {
                    await Release.Task.ConfigureAwait(false);
                }
                return [];
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                }
            }
        }
    }
}
