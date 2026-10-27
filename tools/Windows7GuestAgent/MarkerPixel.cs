namespace CableRoutePlanner.Windows7GuestAgent;

internal static class MarkerPixel
{
    internal static bool IsMatch(byte red, byte green, byte blue)
        => red >= 180 && green <= 90 && blue <= 90;
}
