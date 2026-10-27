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
                if (MarkerPixel.IsMatch(pixel.R, pixel.G, pixel.B))
                {
                    matches++;
                }
            }
        }
        return matches >= 25;
    }

}
