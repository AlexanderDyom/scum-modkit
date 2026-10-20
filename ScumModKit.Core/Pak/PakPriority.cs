namespace ScumModKit.Pak;

/// <summary>
/// Which pak wins when several hold the same file (UE 4.27 FPakPlatformFile, matches SCUM's log):
/// 1. the higher pak order: 3 for Content/Paks (4 for Paks/SCUM-*), plus 100 x patch version for *_P.pak, where the
///    patch version is N+1 for a name ending in _N_P.pak and 1 otherwise;
/// 2. at equal order, the pak mounted first: paks mount in descending, case-insensitive order of their path, so
///    "~mods/pakchunk9999999-X_P.pak" beats "~mods/pakchunk999-Y_P.pak" (strings, not numbers).
/// </summary>
public static class PakPriority
{
    /// <summary>The path the game mounts the pak under (as in SCUM.log).</summary>
    public static string MountPath(string paks, string pak) =>
        "../../../SCUM/Content/Paks/" + Path.GetRelativePath(paks, pak).Replace('\\', '/');

    /// <summary>The N of a name ending in _N_P.pak, or 0 (FPakPlatformFile::Mount reads it from the full mount path).</summary>
    public static int PatchNumber(string mountPath)
    {
        if (!mountPath.EndsWith("_P.pak", StringComparison.OrdinalIgnoreCase)) return 0;
        var stem = mountPath[..^6];
        var i = stem.LastIndexOf('_');
        if (i < 0) return 0;
        var v = stem[(i + 1)..];
        return v.Length > 0 && v.All(char.IsAsciiDigit) && int.TryParse(v, out var n) && n >= 1 ? n : 0;
    }

    public static int Order(string mountPath)
    {
        var order = mountPath.StartsWith("../../../SCUM/Content/Paks/SCUM-", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
        if (mountPath.EndsWith("_P.pak", StringComparison.OrdinalIgnoreCase)) order += 100 * (PatchNumber(mountPath) + 1);
        return order;
    }

    /// <summary>&gt; 0 when pak a wins over pak b.</summary>
    public static int Compare(string paks, string a, string b)
    {
        string ma = MountPath(paks, a), mb = MountPath(paks, b);
        var c = Order(ma).CompareTo(Order(mb));
        return c != 0 ? c : string.CompareOrdinal(ma.ToLowerInvariant(), mb.ToLowerInvariant());
    }

    public static IComparer<string> Comparer(string paks) => Comparer<string>.Create((a, b) => Compare(paks, a, b));
}
