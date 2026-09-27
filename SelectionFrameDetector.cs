namespace HD2_Helper;

internal static class SelectionFrameDetector
{
    private static bool Yellow(Color c) => c.R > 175 && c.G > 145 && c.B < 95 && c.R - c.B > 100;

    private static List<(int Start, int End)> Bands(IEnumerable<int> positions)
    {
        var bands = new List<(int Start, int End)>();
        foreach (int position in positions)
        {
            if (bands.Count > 0 && bands[^1].End == position) bands[^1] = (bands[^1].Start, position + 1);
            else bands.Add((position, position + 1));
        }
        return bands;
    }

    public static List<Rectangle> Find(IconPixelSnapshot pixels)
    {
        var rows = new List<int>();
        for (int y = 0; y < pixels.Height; y++)
        {
            int count = 0;
            for (int x = 0; x < pixels.Width; x++) if (Yellow(pixels.GetPixel(x, y))) count++;
            if (count >= 12) rows.Add(y);
        }
        var bands = Bands(rows);
        var found = new List<Rectangle>();
        foreach (var top in bands)
            foreach (var bottom in bands)
            {
                int height = bottom.End - top.Start;
                if (bottom.Start <= top.End || height < 45 || height > 220) continue;
                var columns = new List<int>();
                for (int x = 0; x < pixels.Width; x++)
                {
                    int count = 0;
                    for (int y = top.Start; y < bottom.End; y++) if (Yellow(pixels.GetPixel(x, y))) count++;
                    if (count >= height / 4) columns.Add(x);
                }
                var sides = Bands(columns);
                foreach (var left in sides)
                    foreach (var right in sides)
                    {
                        int width = right.End - left.Start;
                        if (right.Start <= left.End || width < 45 || width > 220 || Math.Min(width, height) / (double)Math.Max(width, height) < .72) continue;
                        int TopCount((int Start, int End) band)
                        {
                            int best = 0;
                            for (int y = band.Start; y < band.End; y++)
                            {
                                int count = 0;
                                for (int x = left.Start; x < right.End; x++) if (Yellow(pixels.GetPixel(x, y))) count++;
                                best = Math.Max(best, count);
                            }
                            return best;
                        }
                        // Nearby yellow decorations cannot extend the box without supporting all four sides.
                        if (TopCount(top) >= width / 4 && TopCount(bottom) >= width / 4)
                            found.Add(Rectangle.FromLTRB(left.Start, top.Start, right.End, bottom.End));
                    }
            }
        return found.Distinct().ToList();
    }
}
