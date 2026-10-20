// Clones game packages under new names into a stage: the template's files from the game (.uasset/.uexp/.ubulk...),
// the name map renamed with the clone group's renames (CloneRenamer), everything else unchanged — as ScumStudio's
// cloneAsset does (checked byte for byte against its export).
using ScumModKit.Clone;
using ScumModKit.Package;

namespace ScumModKit.Build;

public static class PackageCloner
{
    /// <summary>Clones every package of the groups into <paramref name="stage"/>; returns the files written (pak paths).</summary>
    public static List<string> CloneInto(GameSource game, IEnumerable<CloneGroup> groups, string stage)
    {
        var written = new List<string>();
        foreach (var g in groups)
        {
            var rename = CloneRenamer.For(g.Packages);
            foreach (var p in g.Packages)
                foreach (var (path, data) in Clone(game, p, rename))
                {
                    var target = Path.Combine(stage, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);
                    written.Add(path);
                }
        }
        return written;
    }

    /// <summary>The files of one cloned package: pak path -> bytes.</summary>
    public static List<(string Path, byte[] Data)> Clone(GameSource game, PackageRename p, Func<string, string> rename)
    {
        var files = game.Extract(p.Old).Where(f => Stem(f.Path).Equals(GameSource.PakPath(p.Old), StringComparison.OrdinalIgnoreCase)).ToList();
        var uasset = files.FirstOrDefault(f => f.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase));
        if (uasset.Data == null) throw new FileNotFoundException($"{p.Old}: no .uasset in the game.");
        var uexp = files.FirstOrDefault(f => f.Path.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase)).Data ?? [];

        var pkg = CookedPackage.Load(uasset.Data, uexp);
        Rename(pkg, rename, p.Old);
        var (newUasset, newUexp) = pkg.Save();

        var newStem = GameSource.PakPath(p.New);
        var result = new List<(string, byte[])>();
        foreach (var (path, data) in files)
        {
            var ext = Path.GetExtension(path);
            var bytes = ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase) || ext.Equals(".umap", StringComparison.OrdinalIgnoreCase) ? newUasset
                : ext.Equals(".uexp", StringComparison.OrdinalIgnoreCase) ? newUexp
                : data; // .ubulk, .uptnl: no names inside
            result.Add((newStem + ext, bytes));
        }
        return result;
    }

    /// <summary>Renames the name map; a rename that makes two entries equal is an error (the package would break).</summary>
    public static int Rename(CookedPackage pkg, Func<string, string> rename, string what = "package")
    {
        var changed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < pkg.Names.Count; i++)
        {
            var old = pkg.Names[i].Text;
            var now = rename(old);
            if (now != old) { pkg.SetName(i, now); changed++; }
            if (!seen.Add(now)) throw new InvalidOperationException($"{what}: renaming makes the name {now} twice.");
        }
        return changed;
    }

    private static string Stem(string path) => path[..^Path.GetExtension(path).Length];
}
