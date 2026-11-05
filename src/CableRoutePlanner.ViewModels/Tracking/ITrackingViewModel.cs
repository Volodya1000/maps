using System.ComponentModel;
using CableRoutePlanner.Application;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

public interface ITrackingViewModel : INotifyPropertyChanged
{
    bool IsLocationSupported { get; }
    bool IsLocationEnabled { get; }
    LocationServiceState LocationState { get; }
    LocationMeasurement? CurrentLocation { get; }
    HeadingMeasurement? CurrentHeading { get; }
    RouteProgressState? Progress { get; }
    string CoverageStatus { get; }
    bool HasRoute { get; }
    string CompactProgressText { get; }

    Task SetLocationEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    void SetRoute(RouteAlternative? route);
    void SetCoverage(bool? isInsideCoverage);
    void DisableFollowing();
    void OpenSystemLocationSettings();
}
