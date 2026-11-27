using System.Collections.Concurrent;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.Persistence;

/// <summary>Reads only the small, independently verified DEM shards needed by a route.</summary>
public sealed class TiledElevationProvider : IElevationProvider
{
    private readonly string _elevationDirectory;
    private readonly string? _temporaryDirectory;
    private readonly Lazy<ElevationTileCatalog?> _catalog;
    private readonly ConcurrentDictionary<string, SqliteElevationProvider> _providers = new(StringComparer.Ordinal);

    public TiledElevationProvider(string dataDirectory, string? temporaryDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }
        _elevationDirectory = Path.Combine(dataDirectory, "elevation");
        _temporaryDirectory = temporaryDirectory;
        _catalog = new(() => LoadCatalog(_elevationDirectory));
    }

    public ElevationSource? Source => _catalog.Value?.Source;

    public async Task<IReadOnlyList<double?>> ReadAsync(
        IReadOnlyList<GeoCoordinate> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        cancellationToken.ThrowIfCancellationRequested();
        ElevationTileCatalog? catalog = _catalog.Value;
        double?[] result = new double?[points.Count];
        if (catalog is null || points.Count == 0)
        {
            return result;
        }

        Dictionary<ElevationTileCatalog.Tile, List<int>> groups = [];
        for (int index = 0; index < points.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ElevationTileCatalog.Tile? tile = catalog.Find(points[index]);
            if (tile is not null)
            {
                if (!groups.TryGetValue(tile, out List<int>? indices))
                {
                    indices = [];
                    groups.Add(tile, indices);
                }
                indices.Add(index);
            }
        }

        foreach ((ElevationTileCatalog.Tile tile, List<int> indices) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.Combine(_elevationDirectory, tile.Directory);
            RequireShard(directory, tile.Directory);
            SqliteElevationProvider provider = _providers.GetOrAdd(tile.Directory,
                _ => new SqliteElevationProvider(directory, File.OpenRead, _temporaryDirectory, requireChecksum: true));
            catalog.RequireSource(provider.Source, tile.Directory);
            GeoCoordinate[] positions = indices.Select(index => points[index]).ToArray();
            IReadOnlyList<double?> heights = await provider.ReadAsync(positions, cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < indices.Count; index++)
            {
                result[indices[index]] = heights[index];
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static ElevationTileCatalog? LoadCatalog(string directory)
    {
        string index = Path.Combine(directory, "index.json");
        try
        {
            return ElevationTileCatalog.Parse(File.ReadAllText(index));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static void RequireShard(string directory, string tile)
    {
        foreach (string file in new[] { "elevation.sqlite", "elevation.sqlite.sha256" })
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                throw ElevationTileCatalog.Invalid("tiles.directory", tile, "existing " + file);
            }
        }
    }
}
