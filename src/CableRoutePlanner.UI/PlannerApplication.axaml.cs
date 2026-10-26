using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace CableRoutePlanner.UI;

public sealed partial class PlannerApplication : Avalonia.Application
{
    public Func<Control>? ContentFactory
    {
        get; set;
    }

    public IDisposable? ServiceLifetime
    {
        get; set;
    }

    public string DataDirectory { get; set; } = "";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var content = CreateContent();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = "Планировщик кабельных маршрутов",
#pragma warning disable S1075 // Avalonia resources require an avares URI, not a filesystem path.
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CableRoutePlanner.UI/Assets/app_icon.png"))),
#pragma warning restore S1075
                Width = 1280,
                Height = 850,
                MinWidth = 420,
                MinHeight = 620,
                Content = content
            };
            desktop.Exit += (_, _) => ServiceLifetime?.Dispose();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = content;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public Control CreateContent()
    {
        try
        {
            return ContentFactory?.Invoke() ?? new TextBlock { Text = "Не задана конфигурация приложения." };
        }
        catch (Exception ex)
        {
            return new ScrollViewer
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(28),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock { Text = "Не удалось открыть офлайн-карту", FontSize = 24 },
                        new TextBlock { Text = ex.GetBaseException().Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = $"Каталог данных: {DataDirectory}\n"
                                + "Проверьте наличие manifest.json и geodata.sqlite. "
                                + "Для Desktop путь задаётся CABLEROUTE_DATA. "
                                + "После исправления перезапустите приложение.",
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        }
                    }
                }
            };
        }
    }
}
