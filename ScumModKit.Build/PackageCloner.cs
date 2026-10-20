// Clones game packages under new names into a stage: the template's files from the game (.uasset/.uexp/.ubulk...),
// the name map renamed with the clone group's renames (CloneRenamer), everything else unchanged; and a trader row
// (Economy/Table_TradeableDesc) for every cloned item whose template has one — as ScumStudio's export does (checked
// byte for byte against it).
using ScumModKit.Clone;
using ScumModKit.Package;
using ScumModKit.Properties;
using ScumModKit.Text;

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
        var rows = AddTradeableRows(new Stage(stage, game), groups);
        if (rows > 0) written.Add(GameSource.PakPath(TradeableTable) + ".uasset");
        return written;
    }

    public const string TradeableTable = "/Game/ConZ_Files/Economy/Table_TradeableDesc";

    /// <summary>
    /// Trader rows for clones: a copy of the template's row (key &lt;Template&gt;_C) at the end of the table, with key
    /// &lt;New&gt;_C, TradeableClass = the clone's class and TradingEntryCaption = its name with spaces (culture-invariant).
    /// Clones in the order of their new primary (ignoring case). Returns the number of rows added.
    /// </summary>
    public static int AddTradeableRows(Stage stage, IEnumerable<CloneGroup> groups)
    {
        var files = stage.Game!.Extract(TradeableTable);
        byte[] File(string ext) => files.First(f => f.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).Data;
        var stock = CookedPackage.Load(File(".uasset"), File(".uexp"));
        var stockKeys = DataTable.Read(stock, stock.Exports[0]).Rows.Select(r => stock.NameText(r.Name)).ToHashSet(StringComparer.Ordinal);
        var todo = groups.OrderBy(g => g.NewPrimary, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Packages).Where(p => stockKeys.Contains(p.OldShort + "_C")).ToList();
        if (todo.Count == 0) return 0;

        var added = 0;
        stage.Edit(TradeableTable, pkg =>
        {
            var t = DataTable.Read(pkg, pkg.Exports[0]);
            var byKey = t.Rows.ToDictionary(r => pkg.NameText(r.Name), r => r.Row, StringComparer.Ordinal);
            foreach (var p in todo)
            {
                var key = p.NewShort + "_C";
                if (byKey.ContainsKey(key)) continue;
                var row = byKey[p.OldShort + "_C"].Clone();
                var keyName = new FName(pkg.AddName(key), 0);
                row.Get("TradeableClass").NameValue = new FName(pkg.AddName($"{p.New}.{p.NewShort}_C"), 0);
                if (row.Find("TradingEntryCaption") is { } caption) caption.Value = TextValues.Invariant(p.NewShort.Replace('_', ' '));
                t.Rows.Add((keyName, row));
                byKey[key] = row;
                added++;
            }
            t.Save();
            pkg.SyncGenerationNameCount();
        });
        return added;
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
