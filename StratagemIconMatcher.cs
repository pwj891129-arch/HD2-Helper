namespace HD2_Helper;

// Compare small semantic markings separately from the larger equipment silhouette.
internal sealed class StratagemIconMatcher
{
    private const int Size = 48;
    private readonly Dictionary<string, float[][]> _references = new(StringComparer.OrdinalIgnoreCase);

    public void Remove(string path) => _references.Remove(path);

    public void Prepare(string path)
    {
        if (_references.ContainsKey(path)) return;
        using var bitmap = new Bitmap(path);
        _references[path] = Extract(bitmap);
    }

    internal static float[][] Extract(Bitmap image)
    {
        var result = new float[2][];
        var pixels = new IconPixelSnapshot(image);
        for (int channel = 0; channel < 2; channel++)
        {
            using var mask = new Bitmap(image.Width, image.Height);
            int left = image.Width, top = image.Height, right = -1, bottom = -1;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    Color c = pixels.GetPixel(x, y);
                    int min = Math.Min(c.R, Math.Min(c.G, c.B));
                    int max = Math.Max(c.R, Math.Max(c.G, c.B));
                    bool foreground = c.A >= 128 && (channel == 0
                        ? min >= 150 && max - min <= 65
                        : (c.G >= 70 && c.B >= 70 && c.G - c.R >= 25 && c.B - c.R >= 25)
                          || (c.R >= 100 && c.R - c.G >= 35 && c.R - c.B >= 35));
                    if (!foreground) continue;
                    mask.SetPixel(x, y, Color.White);
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            result[channel] = new float[Size * Size];
            if (right <= left || bottom <= top) continue;
            using var resized = new Bitmap(Size, Size);
            using (var g = Graphics.FromImage(resized))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(mask, new Rectangle(2, 2, Size - 4, Size - 4),
                    new Rectangle(left, top, right - left + 1, bottom - top + 1), GraphicsUnit.Pixel);
            }
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++) result[channel][y * Size + x] = resized.GetPixel(x, y).R / 255f;
        }
        return result;
    }

    public float Compare(float[][] query, string path)
    {
        Prepare(path);
        var reference = _references[path];
        float score = 0;
        for (int channel = 0; channel < 2; channel++)
        {
            float best = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    double error = 0, union = 0;
                    for (int y = 0; y < Size; y++)
                        for (int x = 0; x < Size; x++)
                        {
                            float a = query[channel][y * Size + x];
                            int rx = x + dx, ry = y + dy;
                            float b = rx >= 0 && rx < Size && ry >= 0 && ry < Size ? reference[channel][ry * Size + rx] : 0;
                            error += Math.Abs(a - b);
                            union += Math.Max(a, b);
                        }
                    best = Math.Max(best, union > 0 ? (float)(1 - error / union) : 1f);
                }
            score += best * (channel == 0 ? .7f : .3f);
        }
        return score;
    }
}
