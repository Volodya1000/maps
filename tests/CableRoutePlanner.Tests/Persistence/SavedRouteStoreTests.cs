using System.Text.Json;
using System.Text.Json.Nodes;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using CableRoutePlanner.Persistence;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class SavedRouteStoreTests
{
    [Fact]
    public void RoundTripPreservesEveryAlternativeAndOrderedUserPoints()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute original = SavedRouteDirectory.Snapshot();
        directory.Store.Add(original);
        SavedRoute restored = new JsonSavedRouteStore(directory.Path).Read(original.Id);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
        Assert.Equal(TimeSpan.Zero, restored.AddedUtc.Offset);
    }

    [Fact]
    public void VersionOneRecordStillOpensAndSurvivesAddingAProfile()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute legacy = SavedRouteDirectory.Snapshot();
        JsonObject oldFile = new() { ["Version"] = 1, ["Routes"] = JsonSerializer.SerializeToNode(new[] { legacy }) };
        foreach (JsonNode? alternative in oldFile["Routes"]![0]!["Alternatives"]!.AsArray())
        {
            alternative!.AsObject().Remove("ElevationProfile");
            alternative["Metrics"]!.AsObject().Remove("SurfaceLengthMetres");
        }
        File.WriteAllText(directory.Path, oldFile.ToJsonString());
        SavedRoute additional = SavedRouteDirectory.ElevationSnapshot();
        directory.Store.Add(additional);

        SavedRoute restoredLegacy = directory.Store.Read(legacy.Id);
        Assert.Equal(JsonSerializer.Serialize(legacy), JsonSerializer.Serialize(restoredLegacy));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(directory.Path))!["Version"]!.GetValue<int>());
        SavedRoute restored = directory.Store.Read(additional.Id);
        Assert.Equal(275, restored.Alternatives[1].Metrics.CableLengthMetres(restored.Options.ReserveFraction), 8);
        Assert.Null(restored.Alternatives[0].ElevationProfile!.Samples[1].ElevationMetres);
    }

    [Fact]
    public void SavingAfterTransitionsChangeRejectsStaleElevationAndKeepsPriorSnapshot()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute original = SavedRouteDirectory.ElevationSnapshot();
        directory.Store.Add(original);
        string before = File.ReadAllText(directory.Path);
        RouteAlternative selected = original.Alternatives[1];
        RouteAlternative changed = selected with { TransitionPoints = [selected.Geometry[1]] };
        SavedRoute invalid = original with { Id = Guid.NewGuid(), Alternatives = [changed] };

        Assert.Throws<ArgumentException>(() => directory.Store.Add(invalid));
        Assert.Equal(before, File.ReadAllText(directory.Path));
        Assert.Equal(original.Id, Assert.Single(directory.Store.List()).Id);
    }

    [Fact]
    public void SavingChangedGeometryRejectsSamplesFromThePreviousPath()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute original = SavedRouteDirectory.ElevationSnapshot();
        directory.Store.Add(original);
        string before = File.ReadAllText(directory.Path);
        RouteAlternative selected = original.Alternatives[1];
        RouteAlternative changed = selected with { Geometry = original.Alternatives[0].Geometry };
        SavedRoute invalid = original with { Id = Guid.NewGuid(), Alternatives = [changed] };

        Assert.Throws<ArgumentException>(() => directory.Store.Add(invalid));
        Assert.Equal(before, File.ReadAllText(directory.Path));
    }

    [Theory]
    [InlineData(0, 1, 179)]
    [InlineData(2, 0.1, -179.5)]
    [InlineData(2, 0, 179.25)]
    [InlineData(3, 0, -179.5)]
    [InlineData(4, 1, -170)]
    public void SavedStationsMustFollowEachSegmentAndIncludeVerticesInOrder(int index, double latitude, double longitude)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute snapshot = SavedRouteDirectory.ElevationSnapshot();
        RouteAlternative selected = snapshot.Alternatives[1];
        GeoCoordinate start = new(0, 179);
        GeoCoordinate turn = new(0, -179);
        GeoCoordinate end = new(1, -179);
        GeoCoordinate[] path = [start, start, turn, turn, end, end];
        GeoCoordinate[] stations = [start, new(0, 179.5), new(0, -179.5), turn, end];
        RouteAlternative route = (selected with { Geometry = path }).WithElevationSamples(selected.ElevationProfile!.Source,
            stations.Select((point, distance) => new RouteElevationSample(point, distance, 10, 0)).ToArray(), 4);
        snapshot = snapshot with { Alternatives = [route] };
        directory.Store.Add(snapshot);
        Assert.Equal(stations, directory.Store.Read(snapshot.Id).Alternatives[0].ElevationProfile!.Samples.Select(sample => sample.Coordinate));

        JsonNode file = JsonNode.Parse(File.ReadAllText(directory.Path))!;
        JsonNode sample = file["Routes"]![0]!["Alternatives"]![0]!["ElevationProfile"]!["Samples"]![index]!;
        sample["Coordinate"] = JsonSerializer.SerializeToNode(new GeoCoordinate(latitude, longitude));
        string corrupted = file.ToJsonString();
        File.WriteAllText(directory.Path, corrupted);

        Assert.Throws<PlanningException>(() => directory.Store.Read(snapshot.Id));
        Assert.Equal(corrupted, File.ReadAllText(directory.Path));
    }

    [Theory]
    [InlineData("file", "Version", "1")]
    [InlineData("alternative", "ElevationProfile", null)]
    [InlineData("source", "Version", null)]
    [InlineData("sample", "DistanceMetres", "0")]
    [InlineData("sample", "ElevationMetres", null)]
    [InlineData("sample", "GradePercent", null)]
    [InlineData("profile", "SurfaceLengthMetres", "190")]
    [InlineData("profile", "AscentMetres", "0")]
    [InlineData("profile", "DescentMetres", "null")]
    [InlineData("profile", "HasCompleteCoverage", "false")]
    [InlineData("profile", "IsCableLengthApplicable", "false")]
    [InlineData("metrics", "SurfaceLengthMetres", "190")]
    [InlineData("metrics", "AscentMetres", "0")]
    [InlineData("metrics", "SurfaceLengthMetres", null)]
    [InlineData("alternative", "ElevationProfile", "null")]
    public void InvalidElevationSnapshotIsReportedAndCannotReplaceStorage(string owner, string field, string? replacement)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute route = SavedRouteDirectory.ElevationSnapshot();
        directory.Store.Add(route);
        JsonNode file = JsonNode.Parse(File.ReadAllText(directory.Path))!;
        JsonNode alternative = file["Routes"]![0]!["Alternatives"]![1]!;
        JsonNode profile = alternative["ElevationProfile"]!;
        JsonObject target = (owner switch
        {
            "file" => file,
            "alternative" => alternative,
            "source" => profile["Source"]!,
            "sample" => profile["Samples"]![1]!,
            "profile" => profile,
            "metrics" => alternative["Metrics"]!,
            _ => throw new ArgumentOutOfRangeException(nameof(owner))
        }).AsObject();
        if (replacement is null)
        {
            target.Remove(field);
        }
        else
        {
            target[field] = JsonNode.Parse(replacement);
        }
        string invalid = file.ToJsonString();
        File.WriteAllText(directory.Path, invalid);

        Assert.Throws<PlanningException>(() => directory.Store.Read(route.Id));
        Assert.Throws<PlanningException>(() => directory.Store.Add(SavedRouteDirectory.Snapshot()));
        Assert.Equal(invalid, File.ReadAllText(directory.Path));
    }

    [Fact]
    public void DuplicateCalculationsRemainIndependentAcrossDeletionAndRestart()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute first = SavedRouteDirectory.Snapshot();
        SavedRoute second = first with { Id = Guid.NewGuid(), AddedUtc = first.AddedUtc.AddMinutes(1) };
        directory.Store.Add(first);
        directory.Store.Add(second);
        Assert.Equal(new[] { second.Id, first.Id }, directory.Store.List().Select(route => route.Id));
        directory.Store.Delete(first.Id);
        SavedRoute remaining = Assert.Single(new JsonSavedRouteStore(directory.Path).List());
        Assert.Equal(second.Id, remaining.Id);
        Assert.Equal(second.Alternatives[0].Geometry, remaining.Alternatives[0].Geometry);
    }

    [Fact]
    public void SavingTakesDetachedSnapshotAndRejectsDuplicateIdentity()
    {
        using SavedRouteDirectory directory = new();
        SavedRoute original = SavedRouteDirectory.Snapshot();
        List<GeoCoordinate> geometry = original.Alternatives[0].Geometry.ToList();
        original = original with { Alternatives = [original.Alternatives[0] with { Geometry = geometry }] };
        original = original with { SelectedAlternativeId = original.Alternatives[0].Id };
        directory.Store.Add(original);
        geometry[0] = TestData.P(200, 200);
        Assert.NotEqual(geometry[0], directory.Store.Read(original.Id).Alternatives[0].Geometry[0]);
        Assert.Throws<ArgumentException>(() => directory.Store.Add(original));
        Assert.Single(directory.Store.List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedMutationKeepsTheExistingFileAndRecords(bool delete)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute original = SavedRouteDirectory.Snapshot();
        directory.Store.Add(original);
        string before = File.ReadAllText(directory.Path);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(directory.Path, FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(directory.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            Exception? error = Record.Exception(() =>
            {
                if (delete)
                {
                    directory.Store.Delete(original.Id);
                }
                else
                {
                    directory.Store.Add(original with { Id = Guid.NewGuid() });
                }
            });
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(directory.Path, FileAttributes.Normal);
            }
            else
            {
                File.SetUnixFileMode(directory.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        Assert.Equal(before, File.ReadAllText(directory.Path));
        Assert.Equal(original.Id, Assert.Single(directory.Store.List()).Id);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("date")]
    [InlineData("offset")]
    [InlineData("points")]
    [InlineData("point-id")]
    [InlineData("point-duplicate")]
    [InlineData("alternatives")]
    [InlineData("alternative-id")]
    [InlineData("alternative-duplicate")]
    [InlineData("selection")]
    [InlineData("geometry")]
    [InlineData("name")]
    [InlineData("distance")]
    [InlineData("count")]
    [InlineData("ascent")]
    [InlineData("contact")]
    [InlineData("crossing")]
    [InlineData("passage")]
    [InlineData("options")]
    public void InvalidSnapshotCannotReplaceValidStorage(string field)
    {
        using SavedRouteDirectory directory = new();
        SavedRoute good = SavedRouteDirectory.Snapshot();
        directory.Store.Add(good);
        RouteAlternative first = good.Alternatives[0];
        SavedRoute invalid = field switch
        {
            "id" => good with { Id = Guid.Empty },
            "date" => good with { AddedUtc = default },
            "offset" => good with { AddedUtc = good.AddedUtc.ToOffset(TimeSpan.FromHours(3)) },
            "points" => good with { Points = [] },
            "point-id" => good with { Points = [good.Points[0] with { Id = Guid.Empty }, good.Points[1]] },
            "point-duplicate" => good with { Points = [good.Points[0], good.Points[0]] },
            "alternatives" => good with { Alternatives = [] },
            "alternative-id" => good with { Alternatives = [first with { Id = Guid.Empty }] },
            "alternative-duplicate" => good with { Alternatives = [first, first] },
            "selection" => good with { SelectedAlternativeId = Guid.NewGuid() },
            "options" => good with { Options = new RoutingOptions(ReserveFraction: 2) },
            _ => good with
            {
                SelectedAlternativeId = first.Id,
                Alternatives = [field switch
                {
                    "geometry" => first with { Geometry = [] },
                    "name" => first with { Name = "" },
                    "distance" => first with { Metrics = first.Metrics with { LengthMetres = -1 } },
                    "count" => first with { Metrics = first.Metrics with { RailwayCrossings = -1 } },
                    "ascent" => first with { Metrics = first.Metrics with { AscentMetres = double.NaN } },
                    "contact" => first with { RoadContacts = [new(42, (RoadContactKind)99, [])] },
                    "crossing" => first with { RoadCrossingEvents = [new(42, (RoadCrossingKind)99, default)] },
                    "passage" => first with { UnderpassPassages = [null!] },
                    _ => throw new ArgumentOutOfRangeException(nameof(field))
                }]
            }
        };
        Assert.ThrowsAny<ArgumentException>(() => directory.Store.Add(invalid));
        Assert.Equal(good.Id, Assert.Single(directory.Store.List()).Id);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Version\":99,\"Routes\":[]}")]
    [InlineData("{broken")]
    public void InvalidStorageIsReportedWithoutOverwritingIt(string contents)
    {
        using SavedRouteDirectory directory = new();
        File.WriteAllText(directory.Path, contents);
        Assert.Throws<PlanningException>(() => directory.Store.List());
        Assert.Throws<PlanningException>(() => directory.Store.Add(SavedRouteDirectory.Snapshot()));
        Assert.Equal(contents, File.ReadAllText(directory.Path));
    }
}

internal sealed class SavedRouteDirectory : IDisposable
{
    public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());
    public string Path => System.IO.Path.Combine(DirectoryPath, "saved-routes.json");
    public JsonSavedRouteStore Store => new(Path);

    public SavedRouteDirectory() => Directory.CreateDirectory(DirectoryPath);

    public static SavedRoute Snapshot()
    {
        GeoCoordinate a = TestData.P(10, 10);
        GeoCoordinate b = TestData.P(100, 10);
        GeoCoordinate c = TestData.P(200, 10);
        RouteAlternative first = new(Guid.NewGuid(), "Первый", [a, b, c],
            new(190, 30, 160, 2, 1, 1, 250, 4, 1, 2), [b],
            [new(42, RoadContactKind.SharedSection, [a, b])], [12], [13],
            [new(42, RoadCrossingKind.PedestrianUnderpass, b)], [new(13, a, b)]);
        RouteAlternative second = first with { Id = Guid.NewGuid(), Name = "Второй", Geometry = [a, TestData.P(100, 20), c] };
        return new(Guid.NewGuid(), "Расчёт", new DateTimeOffset(2026, 11, 26, 11, 35, 0, TimeSpan.Zero),
            [new(Guid.NewGuid(), a), new(Guid.NewGuid(), b), new(Guid.NewGuid(), c)],
            [first, second], second.Id, new RoutingOptions(ReserveFraction: 0.1));
    }

    public static SavedRoute ElevationSnapshot()
    {
        SavedRoute route = Snapshot();
        RouteAlternative first = route.Alternatives[0];
        RouteAlternative second = new(route.Alternatives[1].Id, route.Alternatives[1].Name,
            route.Alternatives[1].Geometry, new(190, 0, 190, 0, 0, 0, 190, null));
        ElevationSource source = new("Survey DEM", "2024-Q4", "EGM96", 30, "Survey", new Uri("https://example.org/license"));
        RouteElevationSample[] full = [new(second.Geometry[0], 0, 30, null),
            new(second.Geometry[1], 90, 150, 120.0 / 90 * 100), new(second.Geometry[2], 190, 150, 0)];
        RouteElevationSample[] partial = [new(first.Geometry[0], 0, 30, null),
            new(first.Geometry[1], 90, null, null), new(first.Geometry[2], 190, 150, null)];
        return route with { Alternatives = [first.WithElevationSamples(source, partial, 190), second.WithElevationSamples(source, full, 190)] };
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
