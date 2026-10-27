using System;
using System.Runtime.InteropServices;

namespace CableRoutePlanner.Windows7GuestAgent;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct Win32Rect
{
    internal Win32Rect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    internal int Left { get; }
    internal int Top { get; }
    internal int Right { get; }
    internal int Bottom { get; }
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

internal static class WindowGeometry
{
    internal static bool IsUsable(Win32Rect rectangle)
        => rectangle.Right > rectangle.Left
            && rectangle.Bottom > rectangle.Top
            && rectangle.Width >= 640
            && rectangle.Height >= 480;
}
