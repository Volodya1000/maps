using System.Drawing;

namespace CableRoutePlanner.Windows7GuestAgent;

internal static class RenderAssertions
{
    internal static bool ContainsRedMarker(Bitmap screenshot)
    {
        int matches = 0;
        int left = screenshot.Width / 5;
        int right = screenshot.Width - left;
        int top = screenshot.Height / 5;
        int bottom = screenshot.Height - top;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
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
