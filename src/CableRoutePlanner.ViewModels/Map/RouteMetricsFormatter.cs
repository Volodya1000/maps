using CableRoutePlanner.Domain;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.ViewModels;

public static class RouteMetricsFormatter
{
    public static string Format(RouteAlternative route, RoutingOptions options)
    {
        RouteMetrics metrics = route.Metrics;
        List<string> lines = [];
        AddLengthLines(lines, route);
        string cableBasis = metrics.SurfaceLengthMetres.HasValue ? "по поверхности" : "2D";
        lines.Add($"Кабель с запасом {options.ReserveFraction:P0}: "
            + $"{metrics.CableLengthMetres(options.ReserveFraction):N0} м ({cableBasis})");
        lines.AddRange(
        [
            $"Лес: {metrics.ForestMetres:N0} м · поле: {metrics.FieldMetres:N0} м",
            $"Пересечения дорог: {route.RoadCrossingEvents?.Count ?? metrics.RoadCrossings}",
            $"Пересечений железнодорожных путей: {metrics.RailwayCrossings}",
            $"Разрешённых водных переходов: {metrics.ApprovedWaterCrossings}",
            $"Переходов по мостам: {metrics.PotentialBridgeCrossings}",
            $"Кандидатов по газопроводам: {metrics.PotentialGasPipelineCrossings}",
            $"Взвешенная стоимость: {metrics.WeightedCost:N0} усл. м"
        ]);
        if (route.RoadCrossingEvents is not null)
        {
            foreach (IGrouping<RoadCrossingKind, RoadCrossingEvent> crossings in route.RoadCrossingEvents.GroupBy(crossing => crossing.Kind))
            {
                string name = crossings.Key switch
                {
                    RoadCrossingKind.OverheadGasPipeline => "По надземному газопроводу",
                    RoadCrossingKind.PedestrianUnderpass => "Через подземный пешеходный переход",
                    RoadCrossingKind.Bridge => "По мосту",
                    _ => throw new ArgumentOutOfRangeException(nameof(route))
                };
                int count = crossings.Key == RoadCrossingKind.PedestrianUnderpass
                    ? route.UnderpassPassages?.Count ?? crossings.Count() : crossings.Count();
                lines.Add($"{name}: {count}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatChoiceLength(RouteAlternative route) =>
        route.Metrics.SurfaceLengthMetres is { } surface
            ? $"По поверхности: {surface:N0} м"
            : $"Длина 2D: {route.Metrics.LengthMetres:N0} м";

    private static void AddLengthLines(List<string> lines, RouteAlternative route)
    {
        if (route.ElevationProfile is not { } profile)
        {
            lines.Add($"Горизонтальная длина (2D): {route.Metrics.LengthMetres:N0} м");
            return;
        }

        double horizontal = profile.HorizontalLengthMetres;
        if (profile.SurfaceLengthMetres is { } surface)
        {
            double correction = surface - horizontal;
            double fraction = horizontal > 0 ? correction / horizontal : 0;
            string sign = correction > 0 ? "+" : "";
            lines.Add($"По поверхности: {surface:N1} м · {sign}{correction:N1} м ({sign}{fraction:P1})");
        }
        else
        {
            lines.Add("По поверхности: недоступно.");
        }

        if (!route.Metrics.SurfaceLengthMetres.HasValue)
        {
            lines.Add($"Длина для запаса (2D): {route.Metrics.LengthMetres:N1} м");
        }

        if (!profile.IsCableLengthApplicable && profile.HasCompleteCoverage)
        {
            lines.Add("Высоты переходов оцениваются отдельно.");
        }
    }
}
