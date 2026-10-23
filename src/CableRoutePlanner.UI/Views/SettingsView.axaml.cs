using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using CableRoutePlanner.ViewModels;
using ReactiveUI;

namespace CableRoutePlanner.UI.Views;

public partial class SettingsView : ReactiveUserControl<SettingsViewModel>
{
    public SettingsView()
    {
        InitializeComponent();
        // Bind after NumericUpDown templates initialize, so empty template text cannot
        // mask the persisted values on the first opening. Dispose bindings on navigation.
        this.WhenActivated(lifetime =>
        {
            this.Bind(ViewModel, vm => vm.CellSize, view => view.CellSizeInput.Value).DisposeWith(lifetime);
            this.Bind(ViewModel, vm => vm.ReservePercent, view => view.ReserveInput.Value).DisposeWith(lifetime);
        });
    }
}
