using System.Text.Json;
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

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
