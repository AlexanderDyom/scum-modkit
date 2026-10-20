// The "edits" section of modkit.json: package edits applied to the stage in order. Each edit is a JSON object with "op":
//   {"op": "extract", "package": P}                       copy the game's package into the stage (replacing the stage's)
//   {"op": "copyPackage", "from": P, "to": Q}             the game's package P saved as Q (files renamed only)
//   {"op": "rename", "package": P, "exact": {old: new}, "subs": [[old, new]...], "add": [...], "optional": false}
//   {"op": "addIngredientSlot", "package": P, "export": E, "tagPackage": T, "tag": N, "amount": 1}
//   {"op": "setSlotAmount", "package": P, "export": E, "slot": 0, "amount": 1}
//   {"op": "addIngredientTag", "package": P, "tagPackage": T, "tag": N, "amount": 1.0}
//   {"op": "setObject", "package": P, "export": E, "property": X, "value": 0}        ObjectProperty = package index
//   {"op": "setText", "package": P, "export": E, "property": X, "text": S}           culture-invariant FText
//   {"op": "localize", "namespace": NS, "entries": [{"package", "export", "property", "key", "source",
//        "translations": {culture folder: text}}]}   Base FTexts + SCUM/Content/Localization/Game/<folder>/Game.locres
using System.Text.Json;
using ScumModKit.Package;
using ScumModKit.Text;
using ScumModKit.Properties;

namespace ScumModKit.Build.Ops;

public static class EditList
{
    public static void Apply(Stage stage, IEnumerable<JsonElement> edits)
    {
        var n = 0;
        foreach (var e in edits)
        {
            n++;
            try { Apply(stage, e); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"edit #{n} ({e.GetProperty("op").GetString()}): {ex.Message}", ex);
            }
        }
    }

    private static void Apply(Stage stage, JsonElement e)
    {
        string S(string name) => e.GetProperty(name).GetString() ?? throw new FormatException($"{name} missing");
        int I(string name, int def = 0) => e.TryGetProperty(name, out var v) ? v.GetInt32() : def;
        var op = S("op");
        switch (op)
        {
            case "extract":
                Game(stage).ExtractTo(S("package"), stage.Root);
                break;
            case "copyPackage":
            {
                var from = GameSource.PakPath(S("from"));
                var to = GameSource.PakPath(S("to"));
                foreach (var (path, data) in Game(stage).Extract(S("from")).Where(f => Stem(f.Path).Equals(from, StringComparison.OrdinalIgnoreCase)))
                {
                    var target = Path.Combine(stage.Root, to + Path.GetExtension(path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);
                }
                break;
            }
            case "rename":
            {
                var exact = e.TryGetProperty("exact", out var x) ? x.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : [];
                var subs = e.TryGetProperty("subs", out var s) ? s.EnumerateArray().Select(p => (p[0].GetString()!, p[1].GetString()!)).ToList() : [];
                var add = e.TryGetProperty("add", out var a) ? a.EnumerateArray().Select(p => p.GetString()!).ToList() : [];
                var optional = e.TryGetProperty("optional", out var o) && o.GetBoolean();
                stage.Edit(S("package"), pkg => PackageEdits.RenameNames(pkg, exact, subs, add, optional));
                break;
            }
            case "addIngredientSlot":
                stage.Edit(S("package"), pkg => PackageEdits.AddIngredientSlot(pkg, S("export"), S("tagPackage"), S("tag"), I("amount", 1)));
                break;
            case "setSlotAmount":
                stage.Edit(S("package"), pkg => PackageEdits.SetSlotAmount(pkg, S("export"), I("slot"), I("amount", 1)));
                break;
            case "addIngredientTag":
            {
                var amount = e.TryGetProperty("amount", out var am) ? am.GetSingle() : 1f;
                stage.Edit(S("package"), pkg => PackageEdits.AddIngredientTag(pkg, S("tagPackage"), S("tag"), amount));
                break;
            }
            case "setObject":
                stage.Edit(S("package"), pkg => PackageEdits.SetObjectProperty(pkg, S("export"), S("property"), I("value")));
                break;
            case "setText":
                stage.Edit(S("package"), pkg => SetTexts(pkg, S("export"), new() { [S("property")] = TextValues.Invariant(S("text")) }));
                break;
            case "localize":
                Localize(stage, S("namespace"), e.GetProperty("entries"));
                break;
            default:
                throw new FormatException($"unknown op {op}");
        }
    }

    /// <summary>Every top-level TextProperty of the export named like a key gets that value; each must exist.</summary>
    public static void SetTexts(CookedPackage pkg, string export, Dictionary<string, byte[]> values)
    {
        var ep = ExportProperties.Read(pkg, export);
        var hit = new HashSet<string>();
        foreach (var p in ep.Properties.Where(p => p.Type == "TextProperty" && values.ContainsKey(p.Name)))
        {
            p.Value = values[p.Name];
            hit.Add(p.Name);
        }
        var missing = values.Keys.Where(k => !hit.Contains(k)).ToList();
        if (missing.Count > 0) throw new KeyNotFoundException($"{export}: no TextProperty {string.Join(", ", missing)}.");
        ep.Save();
    }

    /// <summary>
    /// Texts that follow the game language: each entry's TextProperty becomes a Base FText (namespace, key, English
    /// source), and every culture folder gets a Game.locres with the translations of this namespace (locres v3).
    /// </summary>
    public static void Localize(Stage stage, string ns, JsonElement entries)
    {
        var list = entries.EnumerateArray().Select(x => (
            Package: x.GetProperty("package").GetString()!, Export: x.GetProperty("export").GetString()!,
            Property: x.GetProperty("property").GetString()!, Key: x.GetProperty("key").GetString()!,
            Source: x.GetProperty("source").GetString()!,
            Translations: x.GetProperty("translations").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!))).ToList();

        foreach (var group in list.GroupBy(x => (x.Package, x.Export)))
            stage.Edit(group.Key.Package, pkg => SetTexts(pkg, group.Key.Export,
                group.ToDictionary(x => x.Property, x => TextValues.Base(ns, x.Key, x.Source))));

        foreach (var culture in list.SelectMany(x => x.Translations.Keys).Distinct())
        {
            var path = Path.Combine(stage.Root, "SCUM", "Content", "Localization", "Game", culture, "Game.locres");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Locres.WriteV3(ns, list.Select(x => new LocresEntry(x.Key, x.Source, x.Translations[culture])).ToList()));
        }
    }

    private static GameSource Game(Stage stage) => stage.Game ?? throw new InvalidOperationException("needs the game");
    private static string Stem(string path) => path[..^Path.GetExtension(path).Length];
}
