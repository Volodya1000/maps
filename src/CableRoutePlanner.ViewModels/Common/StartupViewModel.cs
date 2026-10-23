using System.Reactive;
using System.Reactive.Disposables;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace CableRoutePlanner.ViewModels;

public sealed class StartupViewModel : ReactiveObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();

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

    public ReactiveCommand<Unit, Unit> Load
    {
        get;
    }

    public StartupViewModel(Func<IProgress<string>, CancellationToken, Task<ShellViewModel>> loader)
    {
        Load = ReactiveCommand.CreateFromTask(async () =>
        {
            IsLoading = true;
            try
            {
                var progress = new Progress<string>(message => Message = message);
                Shell = await loader(progress, _cancellation.Token);
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
        }, this.WhenAnyValue(vm => vm.IsLoading, loading => !loading));
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        Load.Dispose();
        _cancellation.Dispose();
    }
}
