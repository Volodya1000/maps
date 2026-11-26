using System.Globalization;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.ViewModels;

public sealed class SavedRouteRow(SavedRoute snapshot)
{
    public Guid Id => snapshot.Id;
    public string Title => string.IsNullOrWhiteSpace(snapshot.Name) ? "Маршрут от " + AddedText : snapshot.Name;
    public string AddedText => snapshot.AddedUtc.ToLocalTime().ToString("dd.MM.yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string Details => $"{AddedText} · Точек: {snapshot.Points.Count} · Вариантов: {snapshot.Alternatives.Count}";
}
