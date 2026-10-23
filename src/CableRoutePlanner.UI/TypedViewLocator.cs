using CableRoutePlanner.UI.Views;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI;

public sealed class TypedViewLocator(Func<MapView> map, Func<SettingsView> settings) : IViewLocator
{
    public IViewFor? ResolveView<T>(T? viewModel, string? contract = null) => viewModel switch
    {
        MapViewModel vm => Bind(map(), vm),
        SettingsViewModel vm => Bind(settings(), vm),
        _ => throw new ArgumentException("Тип ViewModel не зарегистрирован.", nameof(viewModel))
    };

    private static IViewFor Bind<T>(IViewFor<T> view, T vm)
        where T : class
    {
        view.ViewModel = vm;
        return view;
    }
}
