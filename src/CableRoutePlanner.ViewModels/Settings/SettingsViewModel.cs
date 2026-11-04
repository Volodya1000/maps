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
    private readonly TrackingViewModel? _tracking;
    private readonly CompositeDisposable _lifetime = new();
    private RoutingOptions _saved = new();
    private bool _loadFailed;
    private bool _showLocation;

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
    [Reactive]
    public decimal? MaxRouteLengthMetres
    {
        get; set;
    }
    [Reactive] public string Status { get; private set; } = "";

    public bool IsLocationSupported => _tracking?.IsLocationSupported == true;

    public bool ShowLocation
    {
        get => _showLocation;
        private set => this.RaiseAndSetIfChanged(ref _showLocation, value);
    }

    public string LocationStatus => _tracking?.LocationState.Message ?? "";

    public ReactiveCommand<bool, Unit> SetLocationEnabled
    {
        get; private set;
    }

    public decimal MinimumRouteLengthMetres { get; } = (decimal)RoutingOptions.MinRouteLengthMetres;

    public decimal MaximumRouteLengthMetres { get; } = (decimal)RoutingOptions.MaxRouteLengthLimitMetres;

    public string RouteLengthRangeText =>
        $"Максимальная длина маршрута, м ({MinimumRouteLengthMetres:N0}–{MaximumRouteLengthMetres:N0})";

    public bool HasChanges => CellSize != (decimal)_saved.CellSizeMetres
        || ReservePercent != (decimal)(_saved.ReserveFraction * 100)
        || MaxRouteLengthMetres != (decimal)_saved.MaxRouteLengthMetres;

    public bool CanSave => HasChanges && CellSize is >= 5 and <= 200
        && ReservePercent is >= 0 and <= 100
        && MaxRouteLengthMetres >= MinimumRouteLengthMetres
        && MaxRouteLengthMetres <= MaximumRouteLengthMetres && !_loadFailed;

    public bool CanReset => _loadFailed;

    public ReactiveCommand<Unit, Unit> Save
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> ResetDefaults
    {
        get; private set;
    }

    public SettingsViewModel(IScreen screen, ISettingsStore store, TrackingViewModel? tracking = null)
    {
        HostScreen = screen;
        _store = store;
        _tracking = tracking;
        ShowLocation = tracking?.IsLocationEnabled == true;
        Reload();
        SetupSubscriptions();
        InitializeCommands();
        if (_tracking is not null)
        {
            _tracking.PropertyChanged += TrackingPropertyChanged;
        }
    }

    private void SetupSubscriptions() =>
        this.WhenAnyValue(x => x.CellSize, x => x.ReservePercent, x => x.MaxRouteLengthMetres)
            .Subscribe(_ => NotifyChanges()).DisposeWith(_lifetime);

    [MemberNotNull(nameof(Save), nameof(ResetDefaults), nameof(SetLocationEnabled))]
    private void InitializeCommands()
    {
        Save = ReactiveCommand.Create(SaveSettings, this.WhenAnyValue(x => x.CanSave))
            .DisposeWith(_lifetime);
        ResetDefaults = ReactiveCommand.Create(ResetSettings, this.WhenAnyValue(x => x.CanReset))
            .DisposeWith(_lifetime);
        SetLocationEnabled = ReactiveCommand.CreateFromTask<bool>(SetLocationEnabledAsync)
            .DisposeWith(_lifetime);
    }

    public async Task SetLocationEnabledAsync(bool enabled)
    {
        if (_tracking is null)
        {
            return;
        }

        try
        {
            await _tracking.SetLocationEnabledAsync(enabled);
            ShowLocation = _tracking.IsLocationEnabled;
            LocationStatusChanged();
        }
        catch (Exception ex)
        {
            Status = $"Ошибка включения геолокации: {ex.Message}";
        }
    }

    public void Reload()
    {
        try
        {
            _saved = _store.Load();
            CellSize = (decimal)_saved.CellSizeMetres;
            ReservePercent = (decimal)(_saved.ReserveFraction * 100);
            MaxRouteLengthMetres = (decimal)_saved.MaxRouteLengthMetres;
            _loadFailed = false;
            Status = "";
            NotifyChanges();
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            RoutingOptions defaults = new();
            _saved = defaults;
            CellSize = (decimal)defaults.CellSizeMetres;
            ReservePercent = (decimal)(defaults.ReserveFraction * 100);
            MaxRouteLengthMetres = (decimal)defaults.MaxRouteLengthMetres;
            Status = $"Ошибка загрузки настроек: {ex.Message}";
            NotifyChanges();
        }
    }

    private void ResetSettings()
    {
        try
        {
            _store.Save(new RoutingOptions());
            Reload();
            Status = "Настройки сброшены к значениям по умолчанию.";
        }
        catch (Exception ex)
        {
            Status = $"Ошибка восстановления настроек: {ex.Message}";
        }
    }

    private void SaveSettings()
    {
        try
        {
            // Preserve settings which are not edited by this screen.
            RoutingOptions options = _store.Load() with
            {
                PreferOverheadGasCrossings = true,
                CellSizeMetres = (double)CellSize!.Value,
                ReserveFraction = (double)ReservePercent!.Value / 100,
                MaxRouteLengthMetres = (double)MaxRouteLengthMetres!.Value
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
        this.RaisePropertyChanged(nameof(CanReset));
    }

    private void TrackingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackingViewModel.IsLocationEnabled)
            or nameof(TrackingViewModel.LocationState))
        {
            ShowLocation = _tracking?.IsLocationEnabled == true;
            LocationStatusChanged();
        }
    }

    private void LocationStatusChanged()
    {
        this.RaisePropertyChanged(nameof(IsLocationSupported));
        this.RaisePropertyChanged(nameof(LocationStatus));
    }

    public void Dispose()
    {
        if (_tracking is not null)
        {
            _tracking.PropertyChanged -= TrackingPropertyChanged;
        }

        _lifetime.Dispose();
    }
}
