// Where a mod's clones come from: modkit.json (our project file) or a ScumStudio journal (cloneAsset edits).
using System.Text.Json;
using System.Text.Json.Serialization;
using ScumModKit.Build.Ops;
using ScumModKit.Clone;
using ScumModKit.Registry;

namespace ScumModKit.Build;

/// <summary>
/// modkit.json. Paths inside are relative to the file's folder. Example:
/// <code>
/// {
///   "name": "FoodMod", "pak": "pakchunk901-FoodMod_P", "stage": "build/Client/staging",
///   "clones": [{ "template": "/Game/.../Dish_Salad_Tomato", "new": "/Game/.../Dish_FoodMod_CrazySalad", "with": ["_ES"] }],
///   "journal": "Food_Mod.ssproj/journal.jsonl"
/// }
/// </code>
/// </summary>
public sealed class ModProject
{
    public string? Name { get; set; }
    /// <summary>Pak file name without extension, e.g. pakchunk901-FoodMod_P.</summary>
    public string? Pak { get; set; }
    /// <summary>Stage folder: the files of the pak as they are mounted (SCUM/Content/...).</summary>
    public string? Stage { get; set; }
    public List<CloneSpec> Clones { get; set; } = [];
    /// <summary>Also take the cloneAsset edits of this ScumStudio journal.</summary>
    public string? Journal { get; set; }
    /// <summary>Files or folders copied into the stage: from (relative to modkit.json) -> to (path in the pak).</summary>
    public List<IncludeSpec> Include { get; set; } = [];
    /// <summary>Package edits applied after the clones, includes and cooking edits (see Ops/EditList.cs).</summary>
    public List<JsonElement> Edits { get; set; } = [];
    /// <summary>Cooking recipe edits (recipes, cookbooks, registry, known by default, item texts).</summary>
    public CookingConfig? Cooking { get; set; }

    [JsonIgnore] public string Folder { get; private set; } = ".";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    public static ModProject Load(string path)
    {
        var p = JsonSerializer.Deserialize<ModProject>(File.ReadAllText(path), Json) ?? throw new FormatException($"{path} is empty.");
        p.Folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return p;
    }

    public string? Resolve(string? relative) => relative == null ? null : Path.GetFullPath(Path.Combine(Folder, relative));

    /// <summary>The journal's setAssetValue edits of Text values, as setText edits (empty without a journal).</summary>
    public List<JsonElement> JournalEdits() => Journal == null ? [] : ScumStudioJournal.ReadTextEdits(Resolve(Journal)!);

    /// <summary>Clone groups of the project: its own clones, then the journal's (a later clone of the same primary wins).</summary>
    public List<CloneGroup> CloneGroups()
    {
        var groups = new Dictionary<string, CloneGroup>(StringComparer.OrdinalIgnoreCase);
        if (Journal != null)
            foreach (var g in ScumStudioJournal.ReadClones(Resolve(Journal)!)) groups[g.NewPrimary] = g;
        foreach (var c in Clones)
        {
            var g = c.ToGroup();
            groups[g.NewPrimary] = g;
        }
        return [.. groups.Values];
    }
}

/// <summary>
/// One clone in modkit.json: either template + new (+ suffixes of packages cloned with it, e.g. "_ES"), or an explicit
/// list of packages (the first one is the primary asset).
/// </summary>
public sealed class CloneSpec
{
    public string? Template { get; set; }
    public string? New { get; set; }
    public List<string> With { get; set; } = [];
    public List<PackagePair> Packages { get; set; } = [];

    public CloneGroup ToGroup()
    {
        var list = new List<PackageRename>();
        if (Template != null || New != null)
        {
            if (Template == null || New == null) throw new FormatException("A clone needs both \"template\" and \"new\".");
            list.Add(new PackageRename(Template, New));
            list.AddRange(With.Select(s => new PackageRename(Template + s, New + s)));
        }
        list.AddRange(Packages.Select(p => new PackageRename(p.Old, p.New)));
        if (list.Count == 0) throw new FormatException("A clone without packages.");
        foreach (var r in list) { CheckPath(r.Old); CheckPath(r.New); }
        return new CloneGroup(list[0].New, list);
    }

    private static void CheckPath(string p)
    {
        if (!p.StartsWith('/') || p.Contains('.') || p.EndsWith('/'))
            throw new FormatException($"Not a package path: {p} (expected /Game/Folder/Name, without .Object).");
    }
}

public sealed class IncludeSpec
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class PackagePair
{
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
}

public static class ScumStudioJournal
{
    /// <summary>setAssetValue edits of top-level Text values as modkit setText edits; other value kinds are refused.</summary>
    public static List<JsonElement> ReadTextEdits(string journal)
    {
        var edits = new List<JsonElement>();
        foreach (var text in File.ReadLines(journal))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "edit") continue;
            var edit = root.GetProperty("edit");
            if (edit.GetProperty("op").GetString() != "setAssetValue") continue;
            var kind = edit.GetProperty("valueKind").GetString();
            var path = edit.GetProperty("path").GetString()!;
            if (kind != "Text" || path.Contains('.') || path.Contains('['))
                throw new NotSupportedException($"setAssetValue {kind} {path}: only top-level Text values are supported.");
            edits.Add(JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["op"] = "setText", ["package"] = edit.GetProperty("package").GetString()!, ["export"] = edit.GetProperty("export").GetString()!,
                ["property"] = path, ["text"] = edit.GetProperty("new").GetString()!,
            }));
        }
        return edits;
    }

    /// <summary>cloneAsset edits of a ScumStudio journal.jsonl, by new primary (a later edit of the same primary wins).</summary>
    public static List<CloneGroup> ReadClones(string journal)
    {
        var groups = new Dictionary<string, CloneGroup>(StringComparer.OrdinalIgnoreCase);
        var line = 0;
        foreach (var text in File.ReadLines(journal))
        {
            line++;
            if (string.IsNullOrWhiteSpace(text)) continue;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "edit") continue;
            var edit = root.GetProperty("edit");
            if (edit.GetProperty("op").GetString() != "cloneAsset") continue;
            var primary = edit.GetProperty("newPrimary").GetString() ?? throw new FormatException($"{journal}:{line}: cloneAsset without newPrimary.");
            var packages = edit.GetProperty("packages").EnumerateArray()
                .Select(p => new PackageRename(p.GetProperty("old").GetString()!, p.GetProperty("new").GetString()!)).ToList();
            groups[primary] = new CloneGroup(primary, packages);
        }
        return [.. groups.Values];
    }
}
