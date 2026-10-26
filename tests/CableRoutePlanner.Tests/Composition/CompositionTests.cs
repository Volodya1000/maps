using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CableRoutePlanner.Application;
using CableRoutePlanner.Bootstrap;
using CableRoutePlanner.Composition;
using CableRoutePlanner.UI;
using CableRoutePlanner.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CableRoutePlanner.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void CoreProviderResolvesPlanningWithoutLoadingUiAssemblies()
    {
        using var directory = new TemporaryDirectory();
        using var provider = AppBootstrap.CreateServices(TestData.RealData, Path.Combine(directory.DirectoryPath, "settings.json"));
        var session = provider.GetRequiredService<IPlanningSession>();
        session.Add(TestData.P(50, 150));
        Assert.Single(session.Points);
        Assert.DoesNotContain(typeof(AppBootstrap).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("Avalonia", StringComparison.Ordinal) == true
                || assembly.Name == "CableRoutePlanner.UI");
    }

    [AvaloniaFact]
    public void ConfiguredApplicationBuildsStartupContentAndOwnsServices()
    {
        using var directory = new TemporaryDirectory();
        var app = AvaloniaAppBootstrap.CreateApplication(TestData.RealData, Path.Combine(directory.DirectoryPath, "settings.json"));
        using var lifetime = app.ServiceLifetime;
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
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
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
}
