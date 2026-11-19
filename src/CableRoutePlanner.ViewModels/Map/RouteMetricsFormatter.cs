using CableRoutePlanner.Domain;
using CableRoutePlanner.Application;

namespace CableRoutePlanner.ViewModels;

public static class RouteMetricsFormatter
{
    public static string Format(RouteAlternative route, RoutingOptions options)
    {
        RouteMetrics metrics = route.Metrics;
        List<string> lines =
        [
            $"Длина: {metrics.LengthMetres:N0} м",
            $"Кабель с запасом {options.ReserveFraction:P0}: "
                + $"{metrics.CableLengthMetres(options.ReserveFraction):N0} м",
            $"Лес: {metrics.ForestMetres:N0} м · поле: {metrics.FieldMetres:N0} м",
            $"Пересечения дорог: {route.RoadCrossingEvents?.Count ?? metrics.RoadCrossings}",
            $"Разрешённых водных переходов: {metrics.ApprovedWaterCrossings}",
            $"Переходов по мостам: {metrics.PotentialBridgeCrossings}",
            $"Кандидатов по газопроводам: {metrics.PotentialGasPipelineCrossings}",
            $"Взвешенная стоимость: {metrics.WeightedCost:N0} усл. м"
        ];
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
}
