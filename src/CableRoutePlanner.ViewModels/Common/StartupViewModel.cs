using System.Reactive;
using System.Reactive.Disposables;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class StartupViewModel : ReactiveObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<IProgress<string>, CancellationToken, Task<ShellViewModel>> _loader;

    [Reactive]
    public string Message { get; private set; } = "Подготовка офлайн-карты…";

    [Reactive]
    public bool IsLoading
    {
        get; private set;
    }

    [Reactive]
    public ShellViewModel? Shell
    {
        get; private set;
    }

    public ReactiveCommand<Unit, Unit> Load { get; }

    public StartupViewModel(Func<IProgress<string>, CancellationToken, Task<ShellViewModel>> loader)
    {
        _loader = loader;
        Load = ReactiveCommand.CreateFromTask(LoadAsync, this.WhenAnyValue(vm => vm.IsLoading, loading => !loading));
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            Progress<string> progress = new(message => Message = message);
            Shell = await _loader(progress, _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Message = "Загрузка отменена.";
        }
        catch (Exception error)
        {
            Message = $"Не удалось открыть карту: {error.GetBaseException().Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        Load.Dispose();
        _cancellation.Dispose();
    }
}
