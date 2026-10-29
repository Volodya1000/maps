using CableRoutePlanner.Application;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI;

internal sealed class StartupLoader(
    Func<IProgress<string>, CancellationToken, Task>? prepareData,
    Func<IGeodataRepository> repositoryFactory)
{
    public async Task<ShellViewModel> LoadAsync(
        IProgress<string> progress,
        Func<ShellViewModel> shellFactory,
        CancellationToken cancellationToken)
    {
        if (prepareData is not null)
        {
            await prepareData(progress, cancellationToken);
        }

        progress.Report("Проверка геоданных…");
        await Task.Run(repositoryFactory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return shellFactory();
    }
}
