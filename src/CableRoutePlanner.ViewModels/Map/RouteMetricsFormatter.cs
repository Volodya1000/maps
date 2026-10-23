using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

public static class RouteMetricsFormatter
{
    public static string Format(RouteAlternative route, RoutingOptions options)
    {
        var metrics = route.Metrics;
        return string.Join(
            Environment.NewLine,
            $"Длина: {metrics.LengthMetres:N0} м",
            $"Кабель с запасом {options.ReserveFraction:P0}: "
                + $"{metrics.CableLengthMetres(options.ReserveFraction):N0} м",
            $"Лес: {metrics.ForestMetres:N0} м · поле: {metrics.FieldMetres:N0} м",
            $"Пересечённых объектов дорог: {metrics.RoadCrossings}",
            $"Разрешённых водных переходов: {metrics.ApprovedWaterCrossings}",
            $"Взвешенная стоимость: {metrics.WeightedCost:N0} усл. м",
            "Высотные характеристики: нет данных DEM",
            "Требуются обследование и согласование.");
    }
}
