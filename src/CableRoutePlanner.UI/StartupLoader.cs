using CableRoutePlanner.Application;
using CableRoutePlanner.ViewModels;

namespace CableRoutePlanner.UI;

internal sealed class StartupLoader(
    Func<IProgress<string>, CancellationToken, Task>? prepareData,
    Func<IGeodataRepository> repositoryFactory)
{
    public async Task<ShellViewModel> LoadAsync(
        IProgress<string> progress,
        ShellViewModel shell,
        CancellationToken cancellationToken)
    {
        if (prepareData is not null)
        {
            await prepareData(progress, cancellationToken);
        }

        progress.Report("Проверка геоданных…");
        IGeodataRepository repository = await Task.Run(repositoryFactory, cancellationToken);
        Console.WriteLine(
            $"CRP_OFFLINE_DATA_READY region={repository.Info.RegionId} mode=sqlite");
        return shell;
    }
}
