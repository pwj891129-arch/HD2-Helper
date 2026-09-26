using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HD2_Helper;

// Read pixels once, rather than issuing a GDI+ call for every point in every scan.
internal sealed class IconPixelSnapshot
{
    private readonly byte[] _pixels;
    public int Width { get; }
    public int Height { get; }
    public Size Size => new(Width, Height);

    public IconPixelSnapshot(Bitmap bitmap)
    {
        Width = bitmap.Width;
        Height = bitmap.Height;
        _pixels = new byte[checked(Width * Height * 4)];
        var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), _pixels, y * Width * 4, Width * 4);
        }
        finally { bitmap.UnlockBits(bits); }
    }

    public Color GetPixel(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return Color.FromArgb(_pixels[i + 3], _pixels[i + 2], _pixels[i + 1], _pixels[i]);
    }
}
