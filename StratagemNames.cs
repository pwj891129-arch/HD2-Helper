namespace HD2_Helper;

internal static class StratagemNames
{
    public const string LegacyRecon = "고속 정찰 차량";
    public const string ArtilleryFrv = "M-102 포격 FRV";

    public static string Canonicalize(string name) => name == LegacyRecon ? ArtilleryFrv : name;

    // Existing local-vision references use a hash of the old name; preserve that storage identity.
    public static string StorageName(string name) => name == ArtilleryFrv ? LegacyRecon : name;
}
