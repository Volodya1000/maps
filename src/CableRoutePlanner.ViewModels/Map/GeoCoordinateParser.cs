using System.Globalization;
using CableRoutePlanner.Domain;

namespace CableRoutePlanner.ViewModels;

public static class GeoCoordinateParser
{
    public static bool TryParse(string latitudeText, string longitudeText, out GeoCoordinate coordinate, out string error)
    {
        coordinate = default;
        if (!double.TryParse(latitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude)
            || !double.TryParse(longitudeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
        {
            error = "Введите широту и долготу с десятичной точкой.";
            return false;
        }

        try
        {
            coordinate = new GeoCoordinate(latitude, longitude);
            error = "";
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "Некорректные координаты.";
            return false;
        }
    }
}
