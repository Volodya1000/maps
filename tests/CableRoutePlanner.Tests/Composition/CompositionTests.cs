using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CableRoutePlanner.Application;
using CableRoutePlanner.Infrastructure.Routing;
using CableRoutePlanner.Infrastructure;
using CableRoutePlanner.Domain;
using CableRoutePlanner.UI;
using CableRoutePlanner.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CompositionTests
{
    [Fact]
    public async Task CoreProviderResolvesPlanningWithoutLoadingUiAssemblies()
    {
        using var directory = new TemporaryDirectory();
        using ServiceProvider provider = TestApplication.CreateCoreServices(TestData.RealData, Path.Combine(directory.DirectoryPath, "settings.json"));
        IPlanningSession session = provider.GetRequiredService<IPlanningSession>();
        await session.AddAsync(new GeoCoordinate(53.9490, 27.5180), default);
        Assert.Single(session.Points);
        Assert.DoesNotContain(typeof(GridRoutePlanner).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("Avalonia", StringComparison.Ordinal) == true
                || assembly.Name == "CableRoutePlanner.UI");
    }

    [Fact]
    public async Task InfrastructureRoutePlannerUsesRegisteredGridServices()
    {
        var generator = new CountingGridGenerator();
        var pathfinder = new CountingPathfinder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGeodataRepository>(new MemoryData());
        services.AddInfrastructure();
        services.AddSingleton<IGridGenerator>(generator);
        services.AddSingleton<IGridPathfinder>(pathfinder);
        using ServiceProvider provider = services.BuildServiceProvider();

        IRoutePlanner planner = provider.GetRequiredService<IRoutePlanner>();
        await planner.PlanAsync([TestData.P(50, 150), TestData.P(250, 150)], TestData.Options, default);

        Assert.Same(generator, provider.GetRequiredService<IGridGenerator>());
        Assert.Same(pathfinder, provider.GetRequiredService<IGridPathfinder>());
        Assert.Equal(1, generator.Calls);
        Assert.True(pathfinder.Calls > 0);
    }

    [AvaloniaFact]
    public void ConfiguredApplicationBuildsStartupContentAndOwnsServices()
    {
        using var directory = new TemporaryDirectory();
        ServiceProvider services = TestApplication.CreateServices(TestData.RealData, Path.Combine(directory.DirectoryPath, "settings.json"));
        var app = new PlannerApplication();
        PresentationServiceCollectionExtensions.ConfigureApplication(app, services, TestData.RealData);
        using IDisposable? lifetime = app.ServiceLifetime;
        Assert.IsType<StartupView>(app.CreateContent());
        Assert.Equal(TestData.RealData, app.DataDirectory);
        Assert.NotNull(lifetime);
    }

    [AvaloniaFact]
    public void StartupContentFailureShowsPackageLocationAndUsefulError()
    {
        var app = new PlannerApplication
        {
            DataDirectory = "test-offline-data",
            ContentFactory = () => throw new IOException("missing package")
        };
        var window = new Window { Content = app.CreateContent() };
        window.Show();
        string?[] texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
        Assert.Contains("missing package", texts);
        Assert.Contains(texts, text => text?.Contains("test-offline-data", StringComparison.Ordinal) == true);
        window.Close();
    }

    [AvaloniaFact]
    public void UnconfiguredApplicationExplainsMissingConfiguration()
    {
        var app = new PlannerApplication();
        Assert.Equal("Не задана конфигурация приложения.", Assert.IsType<TextBlock>(app.CreateContent()).Text);
    }

    private sealed class CountingGridGenerator : IGridGenerator
    {
        private readonly GridGenerator _inner = new();
        public int Calls
        {
            get; private set;
        }

        public GridMesh Generate(GeoBounds bounds, RoutingOptions options, CableRoutePlanner.Infrastructure.Geography.LocalProjection projection)
        {
            Calls++;
            return _inner.Generate(bounds, options, projection);
        }
    }

    private sealed class CountingPathfinder : IGridPathfinder
    {
        public int Calls
        {
            get; private set;
        }

        public IReadOnlyList<Coordinate> FindPath(
            Coordinate start,
            Coordinate destination,
            GridMesh mesh,
            CostProfile profile,
            Func<Coordinate, Coordinate, double?> evaluate,
            Func<int, int, double?> gridEdgeCost,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.NotNull(evaluate(start, destination));
            return [start, destination];
        }
    }
}
