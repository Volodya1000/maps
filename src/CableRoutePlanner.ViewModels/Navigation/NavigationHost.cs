using ReactiveUI;
namespace CableRoutePlanner.ViewModels;
public sealed class NavigationHost : ReactiveObject, IScreen { public RoutingState Router { get; } = new(); }
