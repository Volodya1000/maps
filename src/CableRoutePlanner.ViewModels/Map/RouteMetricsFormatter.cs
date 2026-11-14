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
            $"Пересечённых дорог: {metrics.RoadCrossings}",
            $"Разрешённых водных переходов: {metrics.ApprovedWaterCrossings}",
            $"Переходов по мостам: {metrics.PotentialBridgeCrossings}",
            $"Кандидатов по газопроводам: {metrics.PotentialGasPipelineCrossings}",
            $"Взвешенная стоимость: {metrics.WeightedCost:N0} усл. м"
        ];
        if ((route.UnderpassCandidateIds?.Count ?? 0) > 0)
        {
            lines.Add("Подземный переход. Прокладка кабеля требует инженерной проверки");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
