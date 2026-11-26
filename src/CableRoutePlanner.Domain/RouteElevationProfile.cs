using System.Collections.ObjectModel;

namespace CableRoutePlanner.Domain;

/// <summary>Measured horizontal stations and elevations along the completed route.</summary>
public sealed class RouteElevationProfile
{
    private const double TotalsToleranceMetres = 0.000001;

    public RouteElevationProfile(
        ElevationSource? source,
        IReadOnlyList<RouteElevationSample> samples,
        double horizontalLengthMetres,
        double? surfaceLengthMetres,
        double? ascentMetres,
        double? descentMetres,
        bool isCableLengthApplicable)
        : this(source, horizontalLengthMetres, isCableLengthApplicable, Measure(source, samples, horizontalLengthMetres))
    {
        ValidateTotal(surfaceLengthMetres, SurfaceLengthMetres, nameof(surfaceLengthMetres));
        ValidateTotal(ascentMetres, AscentMetres, nameof(ascentMetres));
        ValidateTotal(descentMetres, DescentMetres, nameof(descentMetres));
    }

    private RouteElevationProfile(ElevationSource? source, double horizontalLengthMetres,
        bool isCableLengthApplicable, Measurements measurements)
    {
        Source = source;
        Samples = measurements.Samples;
        HorizontalLengthMetres = horizontalLengthMetres;
        SurfaceLengthMetres = measurements.SurfaceLengthMetres;
        AscentMetres = measurements.AscentMetres;
        DescentMetres = measurements.DescentMetres;
        IsCableLengthApplicable = isCableLengthApplicable;
    }

    public ElevationSource? Source { get; }
    public IReadOnlyList<RouteElevationSample> Samples { get; }
    public double HorizontalLengthMetres { get; }
    public double? SurfaceLengthMetres { get; }
    public double? AscentMetres { get; }
    public double? DescentMetres { get; }
    public bool IsCableLengthApplicable { get; }
    public bool HasCompleteCoverage => SurfaceLengthMetres.HasValue;

    internal static RouteElevationProfile Create(ElevationSource? source, IReadOnlyList<RouteElevationSample> samples,
        double horizontalLengthMetres, bool isCableLengthApplicable) =>
        new(source, horizontalLengthMetres, isCableLengthApplicable, Measure(source, samples, horizontalLengthMetres));

    private static Measurements Measure(ElevationSource? source, IReadOnlyList<RouteElevationSample> samples,
        double horizontalLengthMetres)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(horizontalLengthMetres) || horizontalLengthMetres < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(horizontalLengthMetres), horizontalLengthMetres,
                "Горизонтальная длина должна быть конечной и неотрицательной.");
        }

        ReadOnlyCollection<RouteElevationSample> snapshot = Array.AsReadOnly(samples.ToArray());
        if (snapshot.Count == 0)
        {
            return new(snapshot, null, null, null);
        }

        RouteElevationSample? previous = null;
        bool complete = true;
        double surface = 0;
        double ascent = 0;
        double descent = 0;
        foreach (RouteElevationSample sample in snapshot)
        {
            if (sample is null)
            {
                throw new ArgumentException("Выборка профиля не может быть null.", nameof(samples));
            }

            if (previous is null ? sample.DistanceMetres != 0 : sample.DistanceMetres <= previous.DistanceMetres)
            {
                throw new ArgumentOutOfRangeException(nameof(samples), sample.DistanceMetres,
                    $"Дистанции должны начинаться с нуля и строго возрастать. Предыдущая дистанция: {previous?.DistanceMetres}.");
            }

            if (sample.ElevationMetres is double elevation)
            {
                if (source is null)
                {
                    throw new ArgumentException($"Для высоты {elevation} требуется источник данных.", nameof(source));
                }

                if (previous?.ElevationMetres is double previousElevation)
                {
                    double run = sample.DistanceMetres - previous.DistanceMetres;
                    double rise = elevation - previousElevation;
                    surface += Math.Sqrt(run * run + rise * rise);
                    ascent += Math.Max(rise, 0);
                    descent += Math.Max(-rise, 0);
                }
            }
            else
            {
                complete = false;
            }

            previous = sample;
        }

        double lastStation = snapshot[^1].DistanceMetres;
        if (lastStation != horizontalLengthMetres)
        {
            throw new ArgumentOutOfRangeException(nameof(horizontalLengthMetres), horizontalLengthMetres,
                $"Горизонтальная длина должна совпадать с последней дистанцией профиля ({lastStation} м).");
        }

        if (!complete)
        {
            return new(snapshot, null, null, null);
        }

        ValidateTotal(surface, surface, "surfaceLengthMetres");
        ValidateTotal(ascent, ascent, "ascentMetres");
        ValidateTotal(descent, descent, "descentMetres");
        return new(snapshot, surface, ascent, descent);
    }

    private static void ValidateTotal(double? actual, double? expected, string parameterName)
    {
        if (actual.HasValue != expected.HasValue
            || (actual is double value
                && (!double.IsFinite(value) || value < 0 || Math.Abs(value - expected!.Value) > TotalsToleranceMetres)))
        {
            throw new ArgumentOutOfRangeException(parameterName, actual,
                $"Итог должен совпадать со значением, вычисленным по выборкам профиля: {expected}.");
        }
    }

    private sealed record Measurements(ReadOnlyCollection<RouteElevationSample> Samples,
        double? SurfaceLengthMetres, double? AscentMetres, double? DescentMetres);
}
