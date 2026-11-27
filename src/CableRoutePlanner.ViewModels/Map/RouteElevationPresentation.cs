using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

public sealed record RouteElevationPresentation
{
    private RouteElevationPresentation(RouteAlternative route)
    {
        RouteId = route.Id;
        RouteName = $"Вариант «{route.Name}»";
        Profile = route.ElevationProfile;
        if (Profile is null)
        {
            return;
        }

        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;
        foreach (RouteElevationSample sample in Profile.Samples)
        {
            if (sample.ElevationMetres is { } elevation)
            {
                minimum = Math.Min(minimum, elevation);
                maximum = Math.Max(maximum, elevation);
            }
        }

        if (double.IsFinite(minimum))
        {
            HasChart = true;
            AvailabilityText = Profile.HasCompleteCoverage
                ? "" : "Частичное покрытие. Разрывы означают отсутствие высот.";
            string climb = Profile is { AscentMetres: { } ascent, DescentMetres: { } descent }
                ? $"Суммарный подъём {ascent:N0} м · Спуск {descent:N0} м"
                : "Подъём и спуск недоступны";
            SummaryText = $"{climb}\nМин. {minimum:N0} м · макс. {maximum:N0} м";
        }

        if (Profile.Source is { } source)
        {
            SourceText = $"Шаг измерения ≈ {source.ResolutionMetres:N0} м";
            Attribution = source.Attribution;
        }
    }

    public Guid RouteId { get; }

    public string RouteName { get; }

    public RouteElevationProfile? Profile { get; }

    public bool HasChart { get; }

    public string AvailabilityText { get; } = "Данные высот недоступны.";

    public bool HasAvailabilityNotice => AvailabilityText.Length > 0;

    public string SummaryText { get; } = "";

    public string SourceText { get; } = "";

    public string Attribution { get; } = "";

    public bool HasSource => SourceText.Length > 0;

    public int? CursorIndex { get; internal init; }

    public RouteElevationSample? CursorSample => CursorIndex is { } index ? Profile?.Samples[index] : null;

    public string CursorText
    {
        get
        {
            if (CursorSample is not { } sample)
            {
                return "Выберите точку на графике";
            }

            string elevation = sample.ElevationMetres is { } height ? $"{height:N1} м" : "—";
            string grade = sample.GradePercent is { } percent ? $"{percent:+0.0;-0.0;0.0} %" : "—";
            return $"{sample.DistanceMetres:N0} м от старта · Высота {elevation} · Уклон {grade}";
        }
    }

    internal static RouteElevationPresentation? FromRoute(RouteAlternative? route) =>
        route is null ? null : new RouteElevationPresentation(route);
}
