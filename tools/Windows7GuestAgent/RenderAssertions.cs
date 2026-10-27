using System.Drawing;

namespace CableRoutePlanner.Windows7GuestAgent;

internal static class RenderAssertions
{
    internal static bool ContainsRedMarker(Bitmap screenshot)
    {
        int matches = 0;
        for (int y = 0; y < screenshot.Height; y++)
        {
            for (int x = 0; x < screenshot.Width; x++)
            {
                Color pixel = screenshot.GetPixel(x, y);
                if (pixel.R >= 180 && pixel.G <= 90 && pixel.B <= 90)
                    matches++;
            }
        }
        return matches >= 25;
    }
}
