using System.Drawing;

namespace HD2_Helper;

internal static class WindowHeightSizing
{
    internal static int Normalize(int height) => height == 0 ? 0 : Math.Clamp(height, 430, 2160);

    internal static Size Calculate(int height, int layoutWidth, int layoutHeight, Size available,
        int minimumWidth = 0, int minimumHeight = 0)
    {
        double ratio = (double)layoutWidth / layoutHeight;
        int maximum = Math.Max(1, Math.Min(available.Height, (int)Math.Floor(available.Width / ratio)));
        int minimum = Math.Min(maximum, Math.Max((int)Math.Round(layoutHeight * 0.5),
            Math.Max(minimumHeight, (int)Math.Ceiling(minimumWidth / ratio))));
        int fittedHeight = Math.Clamp(Normalize(height), minimum, maximum);
        return new Size((int)Math.Round(fittedHeight * ratio), fittedHeight);
    }
}
