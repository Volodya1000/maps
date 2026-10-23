using System.Reactive;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class SettingsViewModel : ReactiveObject, IRoutableViewModel, IDisposable
{
    private readonly ISettingsStore _store;

    public string UrlPathSegment => "Настройки";

    public IScreen HostScreen
    {
        get;
    }

    [Reactive]
    public double CellSize
    {
        get; set;
    }

    [Reactive]
    public double ReservePercent
    {
        get; set;
    }

    [Reactive]
    public string Status { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> Save
    {
        get;
    }

    public SettingsViewModel(IScreen screen, ISettingsStore store)
    {
        HostScreen = screen;
        _store = store;
        var settings = store.Load();
        CellSize = settings.CellSizeMetres;
        ReservePercent = settings.ReserveFraction * 100;
        Save = ReactiveCommand.Create(() =>
        {
            try
            {
                _store.Save(new RoutingOptions(CellSize, settings.MaxNodes, ReservePercent / 100));
                Status = "Настройки сохранены. Применяются при следующем расчёте.";
            }
            catch (Exception ex)
            {
                Status = $"Ошибка сохранения: {ex.Message}";
            }
        });
    }

    public void Dispose() => Save.Dispose();
}
