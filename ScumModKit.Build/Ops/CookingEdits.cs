// Cooking recipe edits (SCUM CookingRecipe packages and what makes a recipe reachable: CookingRecipeRegistry, cookbooks,
// CookingSkill), ported from Food_Mod/tools/recipe_edit.py with the same bytes.
using System.Buffers.Binary;
using System.Text.Json;
using ScumModKit.Package;
using ScumModKit.Properties;
using ScumModKit.Text;

namespace ScumModKit.Build.Ops;

/// <summary>An ingredient of a recipe: by its title as the game shows it (source string) or by 0-based index.</summary>
public readonly record struct IngredientRef(string? Title, int Index = -1)
{
    public static implicit operator IngredientRef(string title) => new(title);
    public static implicit operator IngredientRef(int index) => new(null, index);
    public override string ToString() => Title ?? Index.ToString();
}

/// <summary>An item that satisfies an ingredient: object path of its class, whole item or uses, amount.</summary>
public sealed record ItemOption(string Item, bool Whole, int Usage);

public abstract record RecipeOp;
/// <summary>Top-level number (CookingTime, CookingTemperature...); added when the recipe relies on the default.</summary>
public sealed record SetNumber(string Property, double Value) : RecipeOp;
public sealed record RemoveIngredient(string Group, IngredientRef Ingredient) : RecipeOp;
/// <summary>Another item for the ingredient (copy of its first item option).</summary>
public sealed record AddOption(string Group, IngredientRef Ingredient, ItemOption Option) : RecipeOp;
public sealed record SetUsage(string Group, IngredientRef Ingredient, int Usage, int? Option = null) : RecipeOp;
/// <summary>Replaces every variant of the ingredient (liquids too) by items.</summary>
public sealed record SetOptions(string Group, IngredientRef Ingredient, IReadOnlyList<ItemOption> Options) : RecipeOp;
/// <summary>New ingredient at the end of the group; the title is shown verbatim (not localized).</summary>
public sealed record AddIngredient(string Group, string Title, IReadOnlyList<ItemOption> Options) : RecipeOp;
/// <summary>RecipeName / Description shown verbatim.</summary>
public sealed record SetText(string Property, string Text) : RecipeOp;
/// <summary>ResultingFood.Item = object path of the dish class.</summary>
public sealed record SetResult(string Item) : RecipeOp;

public static class CookingEdits
{
    private static readonly Dictionary<string, string> Groups = new() { ["main"] = "MainIngredients", ["optional"] = "OptionalIngredients" };

    // ------------------------------------------------------------------ recipes

    /// <summary>Applies <paramref name="ops"/> to the recipe export (named like the package) of a CookingRecipe package.</summary>
    public static void ApplyRecipe(CookedPackage pkg, string recipeExport, IReadOnlyList<RecipeOp> ops)
    {
        var items = new SortedSet<string>(StringComparer.Ordinal);
        var extra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var op in ops)
            switch (op)
            {
                case SetResult res: items.Add(res.Item); break;
                case AddOption a: items.Add(a.Option.Item); break;
                case SetOptions s: items.UnionWith(s.Options.Select(o => o.Item)); break;
                case AddIngredient a: items.UnionWith(a.Options.Select(o => o.Item)); break;
                case SetNumber n: extra.Add(n.Property); extra.Add("FloatProperty"); break;
            }
        var all = new SortedSet<string>(items, StringComparer.Ordinal);
        all.UnionWith(extra);
        if (all.Count > 0) PackageEdits.RenameNames(pkg, add: all, optional: true);
        var packages = new SortedSet<string>(items.Select(i => i.Split('.')[0]), StringComparer.Ordinal);
        if (packages.Count > 0) AddSoftPackages(pkg, packages);

        var ep = ExportProperties.Read(pkg, recipeExport);
        var r = new RecipeEdit(pkg, ep.Properties);
        foreach (var op in ops) r.Apply(op);
        ep.Save();
    }

    /// <summary>
    /// Package names of referenced items go into the name map (and SoftPackageReferences when the package keeps that
    /// list; cooked recipes keep it empty).
    /// </summary>
    private static void AddSoftPackages(CookedPackage pkg, IEnumerable<string> packages)
    {
        PackageEdits.RenameNames(pkg, add: packages, optional: true);
        if (pkg.SoftPackageReferenceCount != 0)
            throw new NotSupportedException("Adding to a non-empty SoftPackageReferences list is not supported yet.");
    }

    private sealed class RecipeEdit(CookedPackage pkg, PropertyList props)
    {
        private int Idx(string name) => pkg.FindName(name) is var i and >= 0 ? i : throw new KeyNotFoundException($"No name {name}.");

        public void Apply(RecipeOp op)
        {
            switch (op)
            {
                case SetNumber n: SetNumber(n.Property, n.Value); break;
                case RemoveIngredient r: Elements(r.Group).Remove(Ingredient(r.Group, r.Ingredient)); break;
                case AddOption a:
                {
                    var opts = Options(a.Group, a.Ingredient);
                    var src = opts.FirstOrDefault(o => !o.Get("IsResource").Bool)
                              ?? throw new InvalidOperationException($"{a.Ingredient}: only resource options (liquids), nothing to copy.");
                    var copy = src.Clone();
                    SetOption(copy, a.Option.Item, a.Option.Whole, a.Option.Usage);
                    opts.Add(copy);
                    break;
                }
                case SetUsage u:
                {
                    var opts = Options(u.Group, u.Ingredient);
                    for (var i = 0; i < opts.Count; i++)
                        if (u.Option == null || u.Option == i) SetOption(opts[i], usage: u.Usage);
                    break;
                }
                case SetOptions s:
                {
                    var elem = Ingredient(s.Group, s.Ingredient);
                    var tpl = ItemTemplate(elem);
                    elem.Get("IsResource").Bool = false;
                    elem.Get("PossibleIngredient").OpenArray(pkg).Elements = OptionsFrom(tpl, s.Options);
                    break;
                }
                case AddIngredient a:
                {
                    var elems = Elements(a.Group);
                    var tpl = ItemTemplate(null);
                    var elem = elems[0].Clone();
                    elem.Get("IsResource").Bool = false;
                    var title = elem.Get("IngredientTitle");
                    title.Value = TextValues.Invariant(a.Title);
                    elem.Get("PossibleIngredient").OpenArray(pkg).Elements = OptionsFrom(tpl, a.Options);
                    elems.Add(elem);
                    break;
                }
                case SetText t: props.Get(t.Property).Value = TextValues.Invariant(t.Text); break;
                case SetResult r:
                {
                    var rf = props.Get("ResultingFood").OpenStruct(pkg);
                    rf.Get("Item").NameValue = new FName(Idx(r.Item), 0);
                    break;
                }
                default: throw new NotSupportedException($"Unknown recipe op {op}.");
            }
        }

        private void SetNumber(string name, double value)
        {
            var p = props.Find(name);
            if (p == null)
            {
                p = Property.Simple(pkg, name, "FloatProperty", new byte[4]);
                props.Add(p);
            }
            p.Value = p.Type == "FloatProperty" ? BitConverter.GetBytes((float)value) : BitConverter.GetBytes((int)value);
        }

        private List<PropertyList> Elements(string group) =>
            (props.Find(Groups[group]) ?? throw new InvalidOperationException($"The recipe has no {Groups[group]}.")).OpenArray(pkg).Elements;

        private PropertyList Ingredient(string group, IngredientRef which)
        {
            var elems = Elements(group);
            if (which.Title == null) return elems[which.Index];
            var hits = elems.Where(e => TextValues.Source(e.Get("IngredientTitle").Value) == which.Title).ToList();
            if (hits.Count != 1)
                throw new InvalidOperationException($"{group} ingredient {which.Title}: {hits.Count} matches, have " +
                                                    string.Join(", ", elems.Select(e => TextValues.Source(e.Get("IngredientTitle").Value))));
            return hits[0];
        }

        private List<PropertyList> Options(string group, IngredientRef which) =>
            Ingredient(group, which).Get("PossibleIngredient").OpenArray(pkg).Elements;

        private void SetOption(PropertyList opt, string? item = null, bool? whole = null, int? usage = null)
        {
            if (item != null)
            {
                var it = opt.Get("Item");
                BinaryPrimitives.WriteInt32LittleEndian(it.Value, Idx(item));
                BinaryPrimitives.WriteInt32LittleEndian(it.Value.AsSpan(4), 0);
            }
            if (whole != null) opt.Get("ShouldConsumeWhole").Bool = whole.Value;
            if (usage != null) opt.Get("Usage").Value = BitConverter.GetBytes(usage.Value);
        }

        /// <summary>An item (non-liquid) option to copy: from elem if it has one, else from anywhere in the recipe.</summary>
        private PropertyList ItemTemplate(PropertyList? elem)
        {
            var pools = new List<PropertyList>();
            if (elem != null) pools.Add(elem);
            foreach (var g in Groups.Values)
                if (props.Find(g) is { } arr) pools.AddRange(arr.OpenArray(pkg).Elements);
            foreach (var e in pools)
                foreach (var o in e.Get("PossibleIngredient").OpenArray(pkg).Elements)
                    if (!o.Get("IsResource").Bool) return o;
            throw new InvalidOperationException("The recipe has no item option to copy.");
        }

        private List<PropertyList> OptionsFrom(PropertyList tpl, IEnumerable<ItemOption> items) => items.Select(i =>
        {
            var o = tpl.Clone();
            SetOption(o, i.Item, i.Whole, i.Usage);
            return o;
        }).ToList();
    }

    // ------------------------------------------------------------------ recipe lists

    /// <summary>Cookbook (Book_Cooking_*): its recipes += recipes; replace = exactly these recipes (own books).</summary>
    public static void AddToBook(CookedPackage pkg, IReadOnlyList<string> recipes, bool replace = false)
    {
        var export = pkg.Exports.Select(e => pkg.NameText(e.ObjectName)).First(n => n.StartsWith("CookingRecipeCollectionItemTag", StringComparison.Ordinal));
        AppendAssetIds(pkg, export, "_recipes", recipes, replace);
    }

    /// <summary>CookingSkill._recipesKnownByDefault += recipes (a recipe neither there nor in a book never shows up).</summary>
    public static void AddKnownByDefault(CookedPackage pkg, IReadOnlyList<string> recipes) =>
        AppendAssetIds(pkg, "CookingSkill", "_recipesKnownByDefault", recipes, false);

    private static void AppendAssetIds(CookedPackage pkg, string export, string prop, IReadOnlyList<string> recipes, bool replace)
    {
        PackageEdits.RenameNames(pkg, add: recipes, optional: true);
        var ep = ExportProperties.Read(pkg, export);
        var arr = ep.Properties.Get(prop).OpenArray(pkg);
        if (replace)
        {
            var tpl = arr.Elements[0];
            arr.Elements = [tpl];
            tpl.Get("PrimaryAssetName").NameValue = new FName(pkg.FindName(recipes[0]), 0);
        }
        var have = arr.Elements.Select(e => pkg.Names[e.Get("PrimaryAssetName").NameValue.Index].Text).ToHashSet();
        foreach (var rec in recipes)
        {
            if (have.Contains(rec)) continue;
            var e = arr.Elements[0].Clone();
            e.Get("PrimaryAssetName").NameValue = new FName(pkg.FindName(rec), 0);
            arr.Elements.Add(e);
        }
        ep.Save();
    }

    /// <summary>CookingRecipeRegistry._entries += {Enabled: true, AssetId: CookingRecipe:&lt;recipe&gt;} for new recipes.</summary>
    public static void AddRegistryEntries(CookedPackage pkg, IReadOnlyList<string> recipes)
    {
        PackageEdits.RenameNames(pkg, add: recipes, optional: true);
        var ep = ExportProperties.Read(pkg, "CookingRecipeRegistry");
        var elems = ep.Properties.Get("_entries").OpenArray(pkg).Elements;
        string Name(PropertyList e) => pkg.Names[e.Get("AssetId").OpenStruct(pkg).Get("PrimaryAssetName").NameValue.Index].Text;
        var have = elems.Select(Name).ToHashSet();
        foreach (var rec in recipes)
        {
            if (have.Contains(rec)) continue;
            var e = elems[0].Clone();
            e.Get("Enabled").Bool = true;
            e.Get("AssetId").OpenStruct(pkg).Get("PrimaryAssetName").NameValue = new FName(pkg.FindName(rec), 0);
            elems.Add(e);
        }
        ep.Save();
    }

    /// <summary>&lt;Item&gt;_ES: Caption / Description shown verbatim.</summary>
    public static void SetItemTexts(CookedPackage pkg, string caption, string? description = null)
    {
        var export = pkg.Exports.Select(e => pkg.NameText(e.ObjectName)).First(n => n.StartsWith("Default__", StringComparison.Ordinal));
        var ep = ExportProperties.Read(pkg, export);
        ep.Properties.Get("Caption").Value = TextValues.Invariant(caption);
        if (description != null) ep.Properties.Get("Description").Value = TextValues.Invariant(description);
        ep.Save();
    }

    // ------------------------------------------------------------------ JSON (same shape as food_config.py)

    /// <summary>
    /// A recipe op as a JSON array, like the tuples of food_config.py: ["set", prop, value], ["remove", group, ingr],
    /// ["add_option", group, ingr, item, whole, usage], ["usage", group, ingr, usage(, option)],
    /// ["set_options", group, ingr, [[item, whole, usage]...]], ["add_ingredient", group, title, [[...]]],
    /// ["text", prop, text], ["result", item].
    /// </summary>
    public static RecipeOp ParseOp(JsonElement a)
    {
        string S(int i) => a[i].GetString()!;
        IngredientRef I(int i) => a[i].ValueKind == JsonValueKind.Number ? a[i].GetInt32() : S(i);
        List<ItemOption> L(int i) => a[i].EnumerateArray().Select(o => new ItemOption(o[0].GetString()!, o[1].GetBoolean(), o[2].GetInt32())).ToList();
        return S(0) switch
        {
            "set" => new SetNumber(S(1), a[2].GetDouble()),
            "remove" => new RemoveIngredient(S(1), I(2)),
            "add_option" => new AddOption(S(1), I(2), new ItemOption(S(3), a[4].GetBoolean(), a[5].GetInt32())),
            "usage" => new SetUsage(S(1), I(2), a[3].GetInt32(), a.GetArrayLength() > 4 ? a[4].GetInt32() : null),
            "set_options" => new SetOptions(S(1), I(2), L(3)),
            "add_ingredient" => new AddIngredient(S(1), S(2), L(3)),
            "text" => new SetText(S(1), S(2)),
            "result" => new SetResult(S(1)),
            _ => throw new FormatException($"Unknown recipe op {S(0)}."),
        };
    }
}
