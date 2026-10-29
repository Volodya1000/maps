using BruTile;
using BruTile.Cache;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Styles;
using Mapsui.Tiling.Rendering;

namespace CableRoutePlanner.UI.MapAdapters;

public sealed class FadingTileRenderFetchStrategy(TimeProvider? clock = null)
    : IRenderFetchStrategy, IDisposable
{
    public static TimeSpan TransitionDuration { get; } = TimeSpan.FromMilliseconds(200);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<TileIndex, TileState> _states = [];
    private double _lastUnitsPerPixel = double.PositiveInfinity;
    private bool _disposed;
    private long? _animationStarted;
    public bool IsAnimating => !_disposed && _animationStarted is { } started
        && _clock.GetElapsedTime(started) < TransitionDuration;

    private sealed class TileState(RasterFeature original)
    {
        public RasterFeature Original { get; } = original;
        public RasterStyle Style { get; } = new();
        public RasterFeature Display { get; } = new(original.Raster);
        public long Started { get; set; }
        public bool WasCurrent { get; set; }
        public bool CanFade { get; set; }
    }

    public IList<IFeature> Get(MRect extent, double resolution, ITileSchema schema, ITileCache<IFeature?> memoryCache)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (TileIndex key in _states.Keys.ToArray())
        {
            if (!ReferenceEquals(memoryCache.Find(key), _states[key].Original))
            {
                Release(_states[key]);
                _states.Remove(key);
            }
        }
        int level = BruTile.Utilities.GetNearestLevel(schema.Resolutions, resolution);
        double units = schema.Resolutions[level].UnitsPerPixel;
        bool finerLevel = units < _lastUnitsPerPixel;
        _lastUnitsPerPixel = units;
        KeyValuePair<int, Resolution>[] coarser = schema.Resolutions.Where(pair => pair.Value.UnitsPerPixel > units)
            .OrderBy(pair => pair.Value.UnitsPerPixel).ToArray();
        var selected = new Dictionary<TileIndex, TileState>();
        _animationStarted = null;
        foreach (TileInfo tile in schema.GetTileInfos(new Extent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY), level))
        {
            if (memoryCache.Find(tile.Index) is not RasterFeature current)
            {
                if (FindParent(tile, units, schema, memoryCache, coarser) is { } fallback)
                {
                    TileState state = State(fallback.Index, fallback.Feature);
                    state.Style.Opacity = 1;
                    selected[fallback.Index] = state;
                }
                continue;
            }
            TileState detail = State(tile.Index, current);
            (TileIndex Index, RasterFeature Feature)? parent = null;
            if (!detail.WasCurrent || finerLevel)
            {
                detail.Started = _clock.GetTimestamp();
                detail.WasCurrent = true;
                parent = FindParent(tile, units, schema, memoryCache, coarser);
                detail.CanFade = parent.HasValue;
            }
            double progress = _clock.GetElapsedTime(detail.Started).TotalMilliseconds / TransitionDuration.TotalMilliseconds;
            detail.Style.Opacity = 1;
            if (detail.CanFade && progress < 1)
            {
                parent ??= FindParent(tile, units, schema, memoryCache, coarser);
                if (parent is { } backdrop)
                {
                    TileState state = State(backdrop.Index, backdrop.Feature);
                    state.Style.Opacity = 1;
                    selected[backdrop.Index] = state;
                    detail.Style.Opacity = (float)Math.Clamp(progress, 0, 1);
                    _animationStarted = _animationStarted is { } latest ? Math.Max(latest, detail.Started) : detail.Started;
                }
                else
                {
                    detail.CanFade = false;
                }
            }
            selected[tile.Index] = detail;
        }
        return selected.OrderByDescending(pair => schema.Resolutions[pair.Key.Level].UnitsPerPixel)
            .Select(pair => (IFeature)pair.Value.Display).ToArray();
    }

    private TileState State(TileIndex index, RasterFeature feature)
    {
        if (!_states.TryGetValue(index, out TileState? state))
        {
            state = new TileState(feature);
            state.Display.Styles.Add(state.Style);
            _states.Add(index, state);
        }
        return state;
    }

    private static (TileIndex Index, RasterFeature Feature)? FindParent(TileInfo tile, double units,
        ITileSchema schema, ITileCache<IFeature?> cache, IEnumerable<KeyValuePair<int, Resolution>> parentLevels)
    {
        var point = new Extent(tile.Extent.CenterX, tile.Extent.CenterY, tile.Extent.CenterX + units, tile.Extent.CenterY + units);
        foreach (KeyValuePair<int, Resolution> coarser in parentLevels)
        {
            foreach (TileInfo parent in schema.GetTileInfos(point, coarser.Key))
            {
                if (cache.Find(parent.Index) is RasterFeature feature)
                {
                    return (parent.Index, feature);
                }
            }
        }
        return null;
    }

    private static void Release(TileState state)
    {
        foreach (IDisposable bitmap in state.Style.TileCache.Values.OfType<IDisposable>())
        {
            bitmap.Dispose();
        }
        state.Style.TileCache.Clear();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (TileState state in _states.Values)
        {
            Release(state);
        }
        _states.Clear();
        _animationStarted = null;
    }
}
