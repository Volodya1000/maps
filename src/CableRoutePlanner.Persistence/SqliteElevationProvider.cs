using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using Microsoft.Data.Sqlite;

namespace CableRoutePlanner.Persistence;

public sealed class SqliteElevationProvider : IElevationProvider
{
    private const int LegacySchemaVersion = 1;
    private const int CompressedSchemaVersion = 2;
    private const string CompressedBlockEncoding = "zlib-float32-le";
    private const int BlockSize = 128;
    private const int BlockBytes = BlockSize * BlockSize * sizeof(float);
    private const int CachedBlockLimit = 32;
    private const int SnapshotBufferBytes = 128 * 1024;
    // Avoid requiring an extra neighbour when an affine round trip lands on a pixel centre.
    private const double CentreTolerancePixels = 1e-8;
    private const string ReadMetadataSql = "SELECT json FROM metadata WHERE id = 1";
    private const string ReadBlockSql = """
        SELECT samples FROM elevation_blocks
        WHERE grid_id = $grid AND block_row = $row AND block_column = $column
        """;

    private readonly Lazy<Package?> _package;
    private readonly Func<string, Stream> _openDatabaseStream;
    private readonly string _temporaryDirectory;
    private readonly bool _requireChecksum;

    public SqliteElevationProvider(string dataDirectory, string? temporaryDirectory = null)
        : this(dataDirectory, File.OpenRead, temporaryDirectory)
    {
    }

    internal SqliteElevationProvider(
        string dataDirectory, Func<string, Stream> openDatabaseStream, string? temporaryDirectory = null,
        bool requireChecksum = false)
    {
        string path = Path.Combine(dataDirectory, "elevation.sqlite");
        _package = new(() => LoadPackage(path));
        _openDatabaseStream = openDatabaseStream;
        _requireChecksum = requireChecksum;
        _temporaryDirectory = temporaryDirectory ?? Path.GetTempPath();
    }

    public ElevationSource? Source => _package.Value?.Source;

    public Task<IReadOnlyList<double?>> ReadAsync(IReadOnlyList<GeoCoordinate> points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        return Task.Run<IReadOnlyList<double?>>(() => Read(points, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyList<double?>> Read(IReadOnlyList<GeoCoordinate> points, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Package? package = _package.Value;
        cancellationToken.ThrowIfCancellationRequested();
        double?[] result = new double?[points.Count];
        if (package is null || result.Length == 0)
        {
            return result;
        }

        DirectoryInfo snapshotDirectory = Directory.CreateDirectory(
            Path.Combine(_temporaryDirectory, "CableRoutePlanner-elevation-" + Guid.NewGuid().ToString("N")));
        try
        {
            string snapshot = Path.Combine(snapshotDirectory.FullName, "elevation.sqlite");
            await CopyVerifiedSnapshotAsync(package.DatabasePath, snapshot, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteConnection connection = OpenReadOnlyConnection(snapshot);
            connection.Open();
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            // Keep the source, affine and block data in the same package snapshot.
            if (!string.Equals(ReadMetadataJson(command), package.MetadataJson, StringComparison.Ordinal))
            {
                throw Invalid("metadata.json", "метаданные изменились", "перезапуск приложения с обновлённым пакетом");
            }

            command.CommandText = ReadBlockSql;
            BlockReader blocks = new(command, package.Compressed, cancellationToken);
            for (int index = 0; index < result.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GeoCoordinate point = points[index];
                Grid? grid = FindGrid(package.Grids, point.Longitude, point.Latitude, excludedGridId: 0);
                if (grid is not null)
                {
                    result[index] = Sample(grid, package.Grids, blocks, point.Longitude, point.Latitude, acrossSeams: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException error)
        {
            throw Invalid("database", package.DatabasePath, "читаемая SQLite-база высот", error);
        }
        finally
        {
            snapshotDirectory.Delete(recursive: true);
        }
    }

    private static double? Sample(
        Grid grid, Grid[] grids, BlockReader blocks, double longitude, double latitude, bool acrossSeams)
    {
        double row = SnapToCentre((grid.North - latitude) / grid.LatitudeStep - 0.5);
        if (!acrossSeams)
        {
            row = Math.Clamp(row, 0, grid.Height - 1);
        }

        int top = (int)Math.Floor(row);
        int bottom = (int)Math.Ceiling(row);
        (double? upper, double upperLatitude) = SampleRow(grid, grids, blocks, top, longitude, acrossSeams);
        if (upper is null || top == bottom)
        {
            return upper;
        }

        (double? lower, double lowerLatitude) = SampleRow(grid, grids, blocks, bottom, longitude, acrossSeams);
        return Interpolate(upper, lower, (upperLatitude - latitude) / (upperLatitude - lowerLatitude));
    }

    private static (double? Height, double Latitude) SampleRow(
        Grid grid, Grid[] grids, BlockReader blocks, int row, double longitude, bool acrossSeams)
    {
        if (row < 0 || row >= grid.Height)
        {
            bool north = row < 0;
            Grid? neighbour = FindAdjacentBand(grids, grid, longitude, north);
            if (neighbour is null)
            {
                return (null, grid.North - (row + 0.5) * grid.LatitudeStep);
            }

            // Sample the facing native row at the query longitude, since latitude bands
            // can have different column spacings. Then blend using the actual row centres.
            grid = neighbour;
            row = north ? grid.Height - 1 : 0;
        }

        double latitude = grid.North - (row + 0.5) * grid.LatitudeStep;
        double column = SnapToCentre((longitude - grid.West) / grid.LongitudeStep - 0.5);
        if (!acrossSeams)
        {
            column = Math.Clamp(column, 0, grid.Width - 1);
        }

        int left = (int)Math.Floor(column);
        int right = (int)Math.Ceiling(column);
        double? first = ReadPixel(grid, grids, blocks, row, left, acrossSeams);
        double? height = first is null || left == right
            ? first
            : Interpolate(first, ReadPixel(grid, grids, blocks, row, right, acrossSeams), column - left);
        return (height, latitude);
    }

    private static Grid? FindAdjacentBand(Grid[] grids, Grid grid, double longitude, bool north)
    {
        double edge = north ? grid.North : grid.South;
        foreach (Grid candidate in grids)
        {
            double candidateEdge = north ? candidate.South : candidate.North;
            double tolerance = Math.Max(grid.LatitudeStep, candidate.LatitudeStep) * CentreTolerancePixels;
            if (candidate.Id != grid.Id && longitude >= candidate.West && longitude <= candidate.East
                && Math.Abs(candidateEdge - edge) <= tolerance)
            {
                return candidate;
            }
        }

        return null;
    }

    private static double? ReadPixel(Grid grid, Grid[] grids, BlockReader blocks, int row, int column, bool acrossSeams)
    {
        if (column >= 0 && column < grid.Width)
        {
            return blocks.Read(grid.Id, row, column);
        }

        double longitude = grid.West + (column + 0.5) * grid.LongitudeStep;
        double latitude = grid.North - (row + 0.5) * grid.LatitudeStep;
        Grid? neighbour = acrossSeams ? FindGrid(grids, longitude, latitude, grid.Id) : null;
        return neighbour is null ? null : Sample(neighbour, grids, blocks, longitude, latitude, acrossSeams: false);
    }

    private static Grid? FindGrid(Grid[] grids, double longitude, double latitude, int excludedGridId)
    {
        foreach (Grid grid in grids)
        {
            if (grid.Id != excludedGridId && longitude >= grid.West && longitude <= grid.East
                && latitude >= grid.South && latitude <= grid.North)
            {
                return grid;
            }
        }

        return null;
    }

    private static double? Interpolate(double? first, double? second, double weight) =>
        first + (second - first) * weight;

    private static double SnapToCentre(double pixel)
    {
        double nearest = Math.Round(pixel);
        return Math.Abs(nearest - pixel) <= CentreTolerancePixels ? nearest : pixel;
    }

    private static Package? LoadPackage(string path)
    {
        try
        {
            File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            using SqliteConnection connection = OpenReadOnlyConnection(path);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            string json = ReadMetadataJson(command);
            using JsonDocument document = JsonDocument.Parse(json);
            (ElevationSource source, Grid[] grids, bool compressed) = ReadMetadata(document.RootElement);
            command.CommandText = "SELECT grid_id, block_row, block_column, samples FROM elevation_blocks LIMIT 0";
            using SqliteDataReader reader = command.ExecuteReader();
            return new(path, json, source, grids, compressed);
        }
        catch (SqliteException error)
        {
            throw Invalid("database", path, "читаемая SQLite-база высот с таблицами версии 1", error);
        }
        catch (JsonException error)
        {
            throw Invalid("metadata.json", error.Message, "допустимый JSON", error);
        }
    }

    private static SqliteConnection OpenReadOnlyConnection(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false
    }.ToString());

    private async Task CopyVerifiedSnapshotAsync(string path, string snapshot, CancellationToken cancellationToken)
    {
        const string field = "elevation.sqlite.sha256";
        string? expected = await ReadExpectedChecksumAsync(path + ".sha256", cancellationToken).ConfigureAwait(false);
        if (_requireChecksum && expected is null)
        {
            throw Invalid(field, "<missing>", "a SHA-256 identity for every catalog shard");
        }
        if (expected is not null && !IsSha256(expected))
        {
            throw Invalid(field, expected, "SHA-256 из 64 шестнадцатеричных цифр");
        }

        // The same byte stream is copied and hashed, even if the original path is
        // replaced while the snapshot is being prepared.
        await using Stream database = _openDatabaseStream(path);
        await using FileStream output = new(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);
        using IncrementalHash? hash = expected is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[SnapshotBufferBytes];
        int count;
        while ((count = await database.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash?.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        if (hash is not null)
        {
            string actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(field, actual, "SHA-256 " + expected);
            }
        }
    }

    private static async Task<string?> ReadExpectedChecksumAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string ReadMetadataJson(SqliteCommand command)
    {
        command.CommandText = ReadMetadataSql;
        return command.ExecuteScalar() as string
            ?? throw Invalid("metadata.json", "<отсутствует или имеет другой тип>", "строка JSON с идентификатором 1");
    }

    private static (ElevationSource Source, Grid[] Grids, bool Compressed) ReadMetadata(JsonElement root)
    {
        Require(root.ValueKind == JsonValueKind.Object, "metadata.json", root, "объект JSON");
        int schema = ReadInteger(root, "schema_version");
        Require(schema is LegacySchemaVersion or CompressedSchemaVersion, "schema_version", root.GetProperty("schema_version"),
            string.Create(CultureInfo.InvariantCulture, $"версия {LegacySchemaVersion} или {CompressedSchemaVersion}"));
        bool compressed = schema == CompressedSchemaVersion;
        if (compressed)
        {
            Require(ReadString(root, "block_encoding") == CompressedBlockEncoding,
                "block_encoding", root.GetProperty("block_encoding"), CompressedBlockEncoding);
        }
        Require(ReadInteger(root, "block_size") == BlockSize, "block_size", root.GetProperty("block_size"),
            string.Create(CultureInfo.InvariantCulture, $"{BlockSize} пикселей"));
        const string coordinateSystem = "EPSG:4326";
        const string verticalDatum = "EGM2008";
        Require(ReadString(root, "coordinate_system") == coordinateSystem, "coordinate_system",
            root.GetProperty("coordinate_system"), coordinateSystem);
        Require(ReadString(root, "vertical_datum") == verticalDatum, "vertical_datum", root.GetProperty("vertical_datum"),
            verticalDatum);

        JsonElement resolution = Property(root, "resolution_metres", JsonValueKind.Number);
        Require(resolution.TryGetDouble(out double resolutionMetres) && double.IsFinite(resolutionMetres)
            && resolutionMetres > 0, "resolution_metres", resolution, "положительное конечное разрешение в метрах");
        string license = ReadString(root, "license_url");
        if (!Uri.TryCreate(license, UriKind.Absolute, out Uri? licenseUri)
            || (licenseUri.Scheme != Uri.UriSchemeHttps && licenseUri.Scheme != Uri.UriSchemeHttp))
        {
            throw Invalid("license_url", license, "абсолютный адрес HTTP или HTTPS");
        }

        ElevationSource source = new(
            ReadString(root, "source_name"), ReadString(root, "source_version"), verticalDatum,
            resolutionMetres, ReadString(root, "attribution"), licenseUri);
        JsonElement gridList = Property(root, "grids", JsonValueKind.Array);
        Grid[] grids = new Grid[gridList.GetArrayLength()];
        Require(grids.Length != 0, "grids", gridList, "хотя бы одна сетка высот");
        HashSet<int> ids = [];
        for (int index = 0; index < grids.Length; index++)
        {
            Grid grid = ReadGrid(gridList[index], string.Create(CultureInfo.InvariantCulture, $"grids[{index}]"));
            if (!ids.Add(grid.Id))
            {
                throw Invalid(string.Create(CultureInfo.InvariantCulture, $"grids[{index}].id"),
                    grid.Id.ToString(CultureInfo.InvariantCulture), "уникальный идентификатор сетки");
            }

            grids[index] = grid;
        }

        return (source, grids, compressed);
    }

    private static Grid ReadGrid(JsonElement grid, string path)
    {
        Require(grid.ValueKind == JsonValueKind.Object, path, grid, "объект JSON");
        string prefix = path + ".";
        int id = ReadInteger(grid, "id", prefix);
        int width = ReadInteger(grid, "width", prefix);
        int height = ReadInteger(grid, "height", prefix);
        _ = ReadString(grid, "input_file", prefix);
        string sha256 = ReadString(grid, "input_sha256", prefix);
        if (!IsSha256(sha256))
        {
            throw Invalid(prefix + "input_sha256", sha256, "SHA-256 из 64 шестнадцатеричных цифр");
        }

        JsonElement affine = Property(grid, "affine", JsonValueKind.Array, prefix);
        string field = prefix + "affine";
        Require(affine.GetArrayLength() == 6, field, affine, "шесть коэффициентов аффинного преобразования");
        Span<double> coefficients = stackalloc double[6];
        for (int index = 0; index < coefficients.Length; index++)
        {
            JsonElement coefficient = affine[index];
            if (coefficient.ValueKind != JsonValueKind.Number || !coefficient.TryGetDouble(out coefficients[index])
                || !double.IsFinite(coefficients[index]))
            {
                throw Invalid(field, affine.GetRawText(), "конечные числовые коэффициенты");
            }
        }

        Require(coefficients[0] > 0 && coefficients[1] == 0 && coefficients[3] == 0 && coefficients[4] < 0,
            field, affine, "сетка без поворота с направлением на север");
        Grid bounds = new(id, width, height, coefficients[2], coefficients[5], coefficients[0], -coefficients[4]);
        Require(bounds.West >= -180 && bounds.East <= 180 && bounds.East > bounds.West
            && bounds.South >= -90 && bounds.North <= 90 && bounds.North > bounds.South,
            field, affine, string.Create(CultureInfo.InvariantCulture,
                $"размер {width} × {height} и охват ({bounds.West}, {bounds.South}, {bounds.East}, {bounds.North}) в пределах EPSG:4326"));
        return bounds;
    }

    private static int ReadInteger(JsonElement obj, string field, string prefix = "")
    {
        JsonElement element = Property(obj, field, JsonValueKind.Number, prefix);
        Require(element.TryGetInt32(out int value) && value > 0, prefix + field, element, "положительное целое число");
        return value;
    }

    private static string ReadString(JsonElement obj, string field, string prefix = "")
    {
        JsonElement element = Property(obj, field, JsonValueKind.String, prefix);
        string? value = element.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw Invalid(prefix + field, element.GetRawText(), "непустая строка")
            : value;
    }

    private static JsonElement Property(JsonElement obj, string field, JsonValueKind kind, string prefix = "")
    {
        if (!obj.TryGetProperty(field, out JsonElement value))
        {
            throw Invalid(prefix + field, "<отсутствует>", kind.ToString());
        }

        Require(value.ValueKind == kind, prefix + field, value, kind.ToString());
        return value;
    }

    private static void Require(bool condition, string field, JsonElement actual, string rule)
    {
        if (!condition)
        {
            throw Invalid(field, actual.GetRawText(), rule);
        }
    }

    private static InvalidDataException Invalid(string field, string value, string rule, Exception? inner = null)
    {
        InvalidDataException error = new($"Поле {field} в пакете высот имеет значение {value}. Требуется {rule}.", inner);
        error.Data["Field"] = field;
        error.Data["Value"] = value;
        error.Data["Rule"] = rule;
        return error;
    }

    private sealed record Grid(int Id, int Width, int Height, double West, double North, double LongitudeStep, double LatitudeStep)
    {
        public double East => West + Width * LongitudeStep;
        public double South => North - Height * LatitudeStep;
    }

    private sealed record Package(
        string DatabasePath, string MetadataJson, ElevationSource Source, Grid[] Grids, bool Compressed);

    private sealed class BlockReader
    {
        private readonly SqliteCommand _command;
        private readonly SqliteParameter _grid;
        private readonly SqliteParameter _row;
        private readonly SqliteParameter _column;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _compressed;
        private readonly Dictionary<(int Grid, int Row, int Column), byte[]> _blocks = [];
        private readonly Queue<(int Grid, int Row, int Column)> _blockOrder = new();

        public BlockReader(SqliteCommand command, bool compressed, CancellationToken cancellationToken)
        {
            _command = command;
            _grid = command.Parameters.Add("$grid", SqliteType.Integer);
            _row = command.Parameters.Add("$row", SqliteType.Integer);
            _column = command.Parameters.Add("$column", SqliteType.Integer);
            _cancellationToken = cancellationToken;
            _compressed = compressed;
            command.Prepare();
        }

        public double? Read(int grid, int row, int column)
        {
            (int Grid, int Row, int Column) address = (grid, row / BlockSize, column / BlockSize);
            if (!_blocks.TryGetValue(address, out byte[]? samples))
            {
                samples = Load(address);
            }

            int offset = (row % BlockSize * BlockSize + column % BlockSize) * sizeof(float);
            float value = BinaryPrimitives.ReadSingleLittleEndian(samples.AsSpan(offset));
            return float.IsFinite(value) ? value : null;
        }

        private byte[] Load((int Grid, int Row, int Column) address)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _grid.Value = address.Grid;
            _row.Value = address.Row;
            _column.Value = address.Column;
            if (_command.ExecuteScalar() is not byte[] samples)
            {
                throw Invalid("elevation_blocks.samples", string.Create(CultureInfo.InvariantCulture,
                    $"grid {address.Grid}, row {address.Row}, column {address.Column}"),
                    string.Create(CultureInfo.InvariantCulture, $"блок из {BlockBytes} байт"));
            }

            if (_compressed)
            {
                samples = Decompress(samples, address);
            }
            else if (samples.Length != BlockBytes)
            {
                throw InvalidBlock(address);
            }

            if (_blocks.Count == CachedBlockLimit)
            {
                _blocks.Remove(_blockOrder.Dequeue());
            }

            _blocks.Add(address, samples);
            _blockOrder.Enqueue(address);
            return samples;
        }

        private byte[] Decompress(byte[] encoded, (int Grid, int Row, int Column) address)
        {
            byte[] samples = new byte[BlockBytes];
            int length = 0;
            int extra;
            try
            {
                using MemoryStream input = new(encoded, writable: false);
                using ZLibStream zlib = new(input, CompressionMode.Decompress);
                int count;
                while (length < samples.Length && (count = zlib.Read(samples.AsSpan(length))) > 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    length += count;
                }
                extra = zlib.ReadByte();
            }
            catch (InvalidDataException error)
            {
                throw InvalidBlock(address, error);
            }

            // ZLibStream can reach input EOF without reporting a truncated trailer.
            // Check the zlib Adler-32 footer as well as the exact decoded byte count.
            if (length != BlockBytes || extra != -1 || !ValidAdler32(encoded, samples))
            {
                throw InvalidBlock(address);
            }
            return samples;
        }

        private static bool ValidAdler32(byte[] encoded, byte[] samples)
        {
            if (encoded.Length < sizeof(uint))
            {
                return false;
            }
            const uint modulus = 65521;
            uint first = 1;
            uint second = 0;
            foreach (byte sample in samples)
            {
                first = (first + sample) % modulus;
                second = (second + first) % modulus;
            }
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(encoded.Length - sizeof(uint)));
            return expected == ((second << 16) | first);
        }

        private static InvalidDataException InvalidBlock((int Grid, int Row, int Column) address, Exception? inner = null) =>
            Invalid("elevation_blocks.samples", string.Create(CultureInfo.InvariantCulture,
                $"grid {address.Grid}, row {address.Row}, column {address.Column}"),
                string.Create(CultureInfo.InvariantCulture, $"блок из {BlockBytes} байт с заявленным кодированием"), inner);
    }
}
