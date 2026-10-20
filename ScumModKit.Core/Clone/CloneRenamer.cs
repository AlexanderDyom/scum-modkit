// What a clone renames, in package name maps and registry records alike (the rules of ScumStudio's cloneAsset, checked
// against its output): a template package path becomes the new path when it stands as a whole, and a template short name
// becomes the new one when it is a whole word, also as Default__<name> and <name>_C. A short name after '/' belongs to a
// path (maybe another package with the same name) and is left alone, as are names that merely contain it
// (BP_Dish_Salad_Tomato stays when Dish_Salad_Tomato is cloned).
using System.Text.RegularExpressions;

namespace ScumModKit.Clone;

/// <summary>One cloned package: template package path -> new package path (/Game/...).</summary>
public sealed record PackageRename(string Old, string New)
{
    public string OldShort => Old[(Old.LastIndexOf('/') + 1)..];
    public string NewShort => New[(New.LastIndexOf('/') + 1)..];
    public string NewFolder => New[..New.LastIndexOf('/')];
}

/// <summary>
/// A clone: a primary asset and the packages cloned with it (item + _ES + base element + recipes...). The renames of a
/// group apply to every package and registry record of the group.
/// </summary>
public sealed record CloneGroup(string NewPrimary, IReadOnlyList<PackageRename> Packages);

public static class CloneRenamer
{
    private const string Word = "A-Za-z0-9_";

    public static Func<string, string> For(IEnumerable<PackageRename> renames)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var shorts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in renames)
        {
            paths.TryAdd(r.Old, r.New);
            shorts.TryAdd(r.OldShort, r.NewShort);
        }
        if (paths.Count == 0) return s => s;
        static string Alt(IEnumerable<string> keys) =>
            string.Join("|", keys.OrderByDescending(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).Select(Regex.Escape));
        var re = new Regex(
            $"(?<![{Word}])(?<p>{Alt(paths.Keys)})(?![{Word}])" +
            $"|(?<=(?:^|[^{Word}/])(?:Default__)?)(?<s>{Alt(shorts.Keys)})(?=(?:_C)?(?:$|[^{Word}]))",
            RegexOptions.CultureInvariant);
        return s => re.Replace(s, m => m.Groups["p"].Success ? paths[m.Value] : shorts[m.Value]);
    }
}
