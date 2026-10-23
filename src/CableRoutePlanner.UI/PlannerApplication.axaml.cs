using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace CableRoutePlanner.UI;
public sealed partial class PlannerApplication : Avalonia.Application
{
    public Func<Control>? ContentFactory { get; set; }
    public IDisposable? ServiceLifetime { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        var content = ContentFactory?.Invoke() ?? new TextBlock { Text = "Не задана конфигурация приложения." };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window { Title = "Планировщик кабельных маршрутов", Width = 1280, Height = 850,
                MinWidth = 420, MinHeight = 620, Content = content };
            desktop.Exit += (_, _) => ServiceLifetime?.Dispose();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = content;
        base.OnFrameworkInitializationCompleted();
    }
}
