namespace CableRoutePlanner.ViewModels;

public interface IClipboardService
{
    Task SetTextAsync(string text);
}
