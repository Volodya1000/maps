using System.Diagnostics.CodeAnalysis;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class SettingsViewModel : ReactiveObject, IRoutableViewModel, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly CompositeDisposable _lifetime = new();
    private RoutingOptions _saved = new();

    public string UrlPathSegment => "Настройки";
    public IScreen HostScreen { get; }

    // NumericUpDown.Value is decimal?; keep the same type, including an empty input.
    [Reactive]
    public decimal? CellSize
    {
        get; set;
    }
    [Reactive]
    public decimal? ReservePercent
    {
        get; set;
    }
    [Reactive] public string Status { get; private set; } = "";

    public bool HasChanges => CellSize != (decimal)_saved.CellSizeMetres
        || ReservePercent != (decimal)(_saved.ReserveFraction * 100);

    public bool CanSave => HasChanges && CellSize is >= 5 and <= 200
        && ReservePercent is >= 0 and <= 100;

    public ReactiveCommand<Unit, Unit> Save
    {
        get; private set;
    }

    public SettingsViewModel(IScreen screen, ISettingsStore store)
    {
        HostScreen = screen;
        _store = store;
        Reload();
        SetupSubscriptions();
        InitializeCommands();
    }

    private void SetupSubscriptions() =>
        this.WhenAnyValue(x => x.CellSize, x => x.ReservePercent)
            .Subscribe(_ => NotifyChanges()).DisposeWith(_lifetime);

    [MemberNotNull(nameof(Save))]
    private void InitializeCommands()
    {
        Save = ReactiveCommand.Create(SaveSettings, this.WhenAnyValue(x => x.CanSave))
            .DisposeWith(_lifetime);
    }

    public void Reload()
    {
        try
        {
            _saved = _store.Load();
            CellSize = (decimal)_saved.CellSizeMetres;
            ReservePercent = (decimal)(_saved.ReserveFraction * 100);
            Status = "";
            NotifyChanges();
        }
        catch (Exception ex)
        {
            Status = $"Ошибка загрузки настроек: {ex.Message}";
        }
    }

    private void SaveSettings()
    {
        try
        {
            // Preserve settings which are not edited by this screen.
            RoutingOptions options = _store.Load() with
            {
                CellSizeMetres = (double)CellSize!.Value,
                ReserveFraction = (double)ReservePercent!.Value / 100
            };
            _store.Save(options);
            _saved = _store.Load();
            NotifyChanges();
            Status = "Настройки сохранены. Применяются при следующем расчёте.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка сохранения: {ex.Message}";
        }
    }

    private void NotifyChanges()
    {
        this.RaisePropertyChanged(nameof(HasChanges));
        this.RaisePropertyChanged(nameof(CanSave));
    }

    public void Dispose() => _lifetime.Dispose();
}
