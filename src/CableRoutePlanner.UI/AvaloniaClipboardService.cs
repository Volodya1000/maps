using Avalonia.Controls.ApplicationLifetimes;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.UI;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime;
        var topLevel = lifetime switch
        {
            IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            ISingleViewApplicationLifetime single => Avalonia.Controls.TopLevel.GetTopLevel(single.MainView!),
            _ => null
        };
        if (topLevel?.Clipboard is not { } clipboard)
        {
            throw new PlanningException("Буфер обмена недоступен.");
        }

        await clipboard.SetTextAsync(text);
    }
}
