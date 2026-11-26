using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using CableRoutePlanner.Application;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class SavedRoutesViewModel : ReactiveObject, IRoutableViewModel, IDisposable
{
    private readonly ISavedRouteStore _store;
    private readonly CompositeDisposable _lifetime = new();
    public string UrlPathSegment => "Сохранённые";
    public IScreen HostScreen { get; }
    public ObservableCollection<SavedRouteRow> Routes { get; } = [];
    public bool IsEmpty => Routes.Count == 0;
    [Reactive] public string Status { get; private set; } = "";
    [Reactive] public SavedRouteRow? PendingDelete { get; private set; }
    public bool HasPendingDelete => PendingDelete is not null;
    public string DeletePrompt => PendingDelete is { } row ? $"Удалить «{row.Title}» ({row.AddedText})?" : "";
    public ReactiveCommand<Guid, bool> Open { get; }
    public ReactiveCommand<Guid, Unit> RequestDelete { get; }
    public ReactiveCommand<Unit, Unit> ConfirmDelete { get; }
    public ReactiveCommand<Unit, Unit> CancelDelete { get; }
    public ReactiveCommand<Unit, Unit> Refresh { get; }

    public SavedRoutesViewModel(IScreen screen, MapViewModel map, ISavedRouteStore store)
    {
        HostScreen = screen;
        _store = store;
        Open = ReactiveCommand.Create<Guid, bool>(id =>
        {
            try
            {
                bool opened = map.OpenSavedRoute(id);
                Status = opened ? "" : "Не удалось открыть сохранённый маршрут.";
                return opened;
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                Status = "Не удалось открыть маршрут. Текущий маршрут сохранён на карте. " + ex.Message;
                return false;
            }
        }).DisposeWith(_lifetime);
        RequestDelete = ReactiveCommand.Create<Guid>(id => PendingDelete = Routes.FirstOrDefault(row => row.Id == id))
            .DisposeWith(_lifetime);
        CancelDelete = ReactiveCommand.Create(() => { PendingDelete = null; }).DisposeWith(_lifetime);
        ConfirmDelete = ReactiveCommand.Create(Delete, this.WhenAnyValue(vm => vm.PendingDelete).Select(row => row is not null))
            .DisposeWith(_lifetime);
        Refresh = ReactiveCommand.Create(Reload).DisposeWith(_lifetime);
        this.WhenAnyValue(vm => vm.PendingDelete).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(HasPendingDelete));
            this.RaisePropertyChanged(nameof(DeletePrompt));
        }).DisposeWith(_lifetime);
    }

    public void Reload()
    {
        PendingDelete = null;
        try
        {
            IReadOnlyList<SavedRoute> snapshots = _store.List();
            Routes.Clear();
            foreach (SavedRoute snapshot in snapshots)
            {
                Routes.Add(new(snapshot));
            }
            Status = "";
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Status = "Не удалось загрузить сохранённые маршруты: " + ex.Message;
        }
    }

    private void Delete()
    {
        if (PendingDelete is not { } row)
        {
            return;
        }
        try
        {
            _store.Delete(row.Id);
            Reload();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Status = "Не удалось удалить маршрут: " + ex.Message;
        }
    }

    private static bool IsStorageError(Exception error) =>
        error is PlanningException or IOException or UnauthorizedAccessException or ArgumentException;

    public void Dispose() => _lifetime.Dispose();
}
