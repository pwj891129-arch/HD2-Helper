namespace HD2_Helper;

internal sealed class StratagemRuntimeRecognizer
{
    private readonly StratagemIconMatcher _matcher = new();
    private readonly (string Name, string Path)[] _items;

    public StratagemRuntimeRecognizer(string root, IEnumerable<string> names)
    {
        _items = names.Distinct(StringComparer.Ordinal)
            .Select(n => (n, Path.Combine(root, "images", "Stratagems", n + ".png"))).ToArray();
        // Prepare on a worker before any game input; the live path only compares cached arrays.
        foreach (var item in _items) _matcher.Prepare(item.Path);
    }

    internal static string? Choose(IReadOnlyList<(string Name, float Score)> ranked)
    {
        if (ranked.Count < 2) return null;
        float score = ranked[0].Score, margin = score - ranked[1].Score;
        // Tiny HUD icons lose edge detail. A lower absolute score needs a much clearer lead.
        return (score >= .88f && margin >= .06f) || (score >= .84f && margin >= .12f)
            ? ranked[0].Name : null;
    }

    public string? Match(Bitmap image)
        => Choose(Rank(image));

    public (string Name, float Score)[] Rank(Bitmap image)
    {
        var query = StratagemIconMatcher.Extract(image);
        // Include excluded items as competitors too; exclusion must not manufacture confidence.
        return _items.Select(i => (i.Name, Score: _matcher.Compare(query, i.Path)))
            .OrderByDescending(i => i.Score).Take(5).ToArray();
    }

    internal static bool Stable(string? previous, Rectangle previousBounds, string? current, Rectangle currentBounds, long elapsedMs)
        => previous != null && current == previous && elapsedMs >= 25 && elapsedMs <= 250
            && previousBounds.Width > 0 && previousBounds.Height > 0
            && Math.Abs(previousBounds.X - currentBounds.X) <= 2
            && Math.Abs(previousBounds.Y - currentBounds.Y) <= 2
            && Math.Abs(previousBounds.Width - currentBounds.Width) <= 2
            && Math.Abs(previousBounds.Height - currentBounds.Height) <= 2;
}
