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

/// <summary>
/// A variant of an ingredient: object path of the item class, whole item or uses, amount (Usage). Liquids: Resource =
/// class of the game resource (/Game/ConZ_Files/GameResources/Food/Liquids/Oil.Oil_C), Liters = how much; Item is then
/// the container item (e.g. VegetableOil), or "" for any container (None, as most stock liquid variants).
/// Liters on an item variant only sets the field.
/// </summary>
public sealed record ItemOption(string Item, bool Whole, int Usage, double? Liters = null, string? Resource = null);

public abstract record RecipeOp;
/// <summary>Top-level number (CookingTime, CookingTemperature...); added when the recipe relies on the default.</summary>
public sealed record SetNumber(string Property, double Value) : RecipeOp;
public sealed record RemoveIngredient(string Group, IngredientRef Ingredient) : RecipeOp;
/// <summary>Another item for the ingredient (copy of its first item option).</summary>
public sealed record AddOption(string Group, IngredientRef Ingredient, ItemOption Option) : RecipeOp;
public sealed record SetUsage(string Group, IngredientRef Ingredient, int Usage, int? Option = null) : RecipeOp;
/// <summary>Liters of every variant of the ingredient (or of one, by 0-based index): the amount of a liquid.</summary>
public sealed record SetLiters(string Group, IngredientRef Ingredient, double Liters, int? Option = null) : RecipeOp;
/// <summary>Replaces every variant of the ingredient (liquids too) by items.</summary>
public sealed record SetOptions(string Group, IngredientRef Ingredient, IReadOnlyList<ItemOption> Options) : RecipeOp;
/// <summary>New ingredient at the end of the group; the title is shown verbatim (not localized).</summary>
public sealed record AddIngredient(string Group, string Title, IReadOnlyList<ItemOption> Options) : RecipeOp;
/// <summary>RecipeName / Description shown verbatim.</summary>
public sealed record SetText(string Property, string Text) : RecipeOp;
/// <summary>ResultingFood.Item = object path of the dish class.</summary>
public sealed record SetResult(string Item) : RecipeOp;
/// <summary>The ingredient's title shown verbatim (not localized); later refs by title use the new text.</summary>
public sealed record SetTitle(string Group, IngredientRef Ingredient, string Title) : RecipeOp;
/// <summary>NutrientInclusionFactor of every variant (or of one, by 0-based index): how much of the item counts for nutrition.</summary>
public sealed record SetNutrient(string Group, IngredientRef Ingredient, double Factor, int? Option = null) : RecipeOp;
/// <summary>Utility gameplay tags (where it is cooked): Cooking.Utility.Pan, Cooking.Utility.Oven...</summary>
public sealed record SetUtilities(IReadOnlyList<string> Tags) : RecipeOp;

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
                case AddOption a: items.UnionWith(new[] { a.Option.Item }.Where(i => i.Length > 0)); break;
                case SetOptions s: items.UnionWith(s.Options.Select(o => o.Item).Where(i => i.Length > 0)); break;
                case AddIngredient a: items.UnionWith(a.Options.Select(o => o.Item).Where(i => i.Length > 0)); break;
                case SetNumber n: extra.Add(n.Property); extra.Add("FloatProperty"); break;
                case SetNutrient: extra.Add("NutrientInclusionFactor"); extra.Add("FloatProperty"); break;
                case SetUtilities u: extra.UnionWith(u.Tags); break;
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
                    var src = a.Option.Resource != null
                        ? opts.FirstOrDefault(o => o.Get("IsResource").Bool) ?? opts[0]
                        : opts.FirstOrDefault(o => !o.Get("IsResource").Bool)
                          ?? throw new InvalidOperationException($"{a.Ingredient}: only resource options (liquids), nothing to copy.");
                    var copy = src.Clone();
                    Fill(copy, a.Option);
                    opts.Add(copy);
                    if (a.Option.Resource != null) SlotIsResource(Ingredient(a.Group, a.Ingredient));
                    break;
                }
                case SetLiters l:
                {
                    var opts = Options(l.Group, l.Ingredient);
                    for (var i = 0; i < opts.Count; i++)
                        if (l.Option == null || l.Option == i) opts[i].Get("Liters").Value = BitConverter.GetBytes((float)l.Liters);
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
                    var liquid = s.Options.Any(o => o.Resource != null) ? ResourceTemplate(elem) : null;
                    elem.Get("IsResource").Bool = false;
                    elem.Get("PossibleIngredient").OpenArray(pkg).Elements = OptionsFrom(tpl, liquid, s.Options);
                    if (s.Options.Any(o => o.Resource != null)) SlotIsResource(elem);
                    break;
                }
                case AddIngredient a:
                {
                    var elems = Elements(a.Group);
                    var tpl = ItemTemplate(null);
                    var liquid = a.Options.Any(o => o.Resource != null) ? ResourceTemplate(null) : null;
                    var elem = elems[0].Clone();
                    elem.Get("IsResource").Bool = false;
                    var title = elem.Get("IngredientTitle");
                    title.Value = TextValues.Invariant(a.Title);
                    elem.Get("PossibleIngredient").OpenArray(pkg).Elements = OptionsFrom(tpl, liquid, a.Options);
                    if (a.Options.Any(o => o.Resource != null)) SlotIsResource(elem);
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
                case SetTitle t: Ingredient(t.Group, t.Ingredient).Get("IngredientTitle").Value = TextValues.Invariant(t.Title); break;
                case SetNutrient n:
                {
                    var opts = Options(n.Group, n.Ingredient);
                    for (var i = 0; i < opts.Count; i++)
                    {
                        if (n.Option != null && n.Option != i) continue;
                        var f = opts[i].Find("NutrientInclusionFactor");
                        if (f == null) opts[i].Insert(opts[i].Count, f = Property.Simple(pkg, "NutrientInclusionFactor", "FloatProperty", new byte[4]));
                        f.Value = BitConverter.GetBytes((float)n.Factor);
                    }
                    break;
                }
                case SetUtilities u:
                {
                    // FGameplayTagContainer, natively serialized: int32 count + FName per tag
                    var p = props.Find("Utility") ?? throw new InvalidOperationException("The recipe has no Utility tags to replace.");
                    if (p.Type != "StructProperty") throw new NotSupportedException($"Utility is a {p.Type}.");
                    var v = new byte[4 + 8 * u.Tags.Count];
                    BinaryPrimitives.WriteInt32LittleEndian(v, u.Tags.Count);
                    for (var i = 0; i < u.Tags.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4 + 8 * i), Idx(u.Tags[i]));
                    p.Value = v;
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
                BinaryPrimitives.WriteInt32LittleEndian(it.Value, Idx(item.Length > 0 ? item : "None")); // "" = None: any container
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

        /// <summary>A liquid option to copy: from elem if it has one, else from anywhere in the recipe, else null.</summary>
        private PropertyList? ResourceTemplate(PropertyList? elem)
        {
            var pools = new List<PropertyList>();
            if (elem != null) pools.Add(elem);
            foreach (var g in Groups.Values)
                if (props.Find(g) is { } arr) pools.AddRange(arr.OpenArray(pkg).Elements);
            return pools.SelectMany(e => e.Get("PossibleIngredient").OpenArray(pkg).Elements).FirstOrDefault(o => o.Get("IsResource").Bool);
        }

        /// <summary>Item variants copy the item template, liquid variants the liquid one (or the item one, made liquid).</summary>
        private List<PropertyList> OptionsFrom(PropertyList tpl, PropertyList? liquid, IEnumerable<ItemOption> items) => items.Select(i =>
        {
            var o = (i.Resource != null ? liquid ?? tpl : tpl).Clone();
            Fill(o, i);
            return o;
        }).ToList();

        private void Fill(PropertyList opt, ItemOption i)
        {
            SetOption(opt, i.Item, i.Whole, i.Usage);
            if (i.Resource != null)
            {
                var dot = i.Resource.LastIndexOf('.');
                if (dot < 0 || !i.Resource.StartsWith('/')) throw new FormatException($"Resource {i.Resource}: expected /Game/Folder/Name.Name_C.");
                opt.Get("IsResource").Bool = true;
                opt.Get("ResourceType").ObjectIndex =
                    PackageEdits.AddImport(pkg, i.Resource[..dot], i.Resource[(dot + 1)..], "/Script/Engine", "BlueprintGeneratedClass");
            }
            if (i.Liters != null) opt.Get("Liters").Value = BitConverter.GetBytes((float)i.Liters.Value);
        }

        /// <summary>The ingredient counts as a liquid slot when all its variants are liquids (as in the stock recipes).</summary>
        private void SlotIsResource(PropertyList elem) =>
            elem.Get("IsResource").Bool = elem.Get("PossibleIngredient").OpenArray(pkg).Elements.All(o => o.Get("IsResource").Bool);
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
    /// ["text", prop, text], ["result", item], ["title", group, ingr, text], ["nutrient", group, ingr, factor(, option)],
    /// ["utility", [tag...]], ["liters", group, ingr, liters(, option)].
    /// A variant is [item, whole, usage(, liters(, resource))] or {"item", "whole", "usage", "liters", "resource"};
    /// add_option takes liters and resource as 7th and 8th elements.
    /// </summary>
    public static RecipeOp ParseOp(JsonElement a)
    {
        string S(int i) => a[i].GetString()!;
        IngredientRef I(int i) => a[i].ValueKind == JsonValueKind.Number ? a[i].GetInt32() : S(i);
        static double? D(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
        static string? R(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        static ItemOption O(JsonElement o) => o.ValueKind == JsonValueKind.Object
            ? new ItemOption(o.GetProperty("item").GetString() ?? "", o.GetProperty("whole").GetBoolean(), o.GetProperty("usage").GetInt32(),
                o.TryGetProperty("liters", out var l) ? D(l) : null, o.TryGetProperty("resource", out var r) ? R(r) : null)
            : new ItemOption(o[0].GetString() ?? "", o[1].GetBoolean(), o[2].GetInt32(),
                o.GetArrayLength() > 3 ? D(o[3]) : null, o.GetArrayLength() > 4 ? R(o[4]) : null);
        List<ItemOption> L(int i) => a[i].EnumerateArray().Select(O).ToList();
        return S(0) switch
        {
            "set" => new SetNumber(S(1), a[2].GetDouble()),
            "remove" => new RemoveIngredient(S(1), I(2)),
            "add_option" => new AddOption(S(1), I(2), new ItemOption(S(3), a[4].GetBoolean(), a[5].GetInt32(),
                a.GetArrayLength() > 6 ? D(a[6]) : null, a.GetArrayLength() > 7 ? R(a[7]) : null)),
            "usage" => new SetUsage(S(1), I(2), a[3].GetInt32(), a.GetArrayLength() > 4 ? a[4].GetInt32() : null),
            "set_options" => new SetOptions(S(1), I(2), L(3)),
            "add_ingredient" => new AddIngredient(S(1), S(2), L(3)),
            "text" => new SetText(S(1), S(2)),
            "result" => new SetResult(S(1)),
            "title" => new SetTitle(S(1), I(2), S(3)),
            "nutrient" => new SetNutrient(S(1), I(2), a[3].GetDouble(), a.GetArrayLength() > 4 ? a[4].GetInt32() : null),
            "liters" => new SetLiters(S(1), I(2), a[3].GetDouble(), a.GetArrayLength() > 4 ? a[4].GetInt32() : null),
            "utility" => new SetUtilities(a[1].EnumerateArray().Select(t => t.GetString()!).ToList()),
            _ => throw new FormatException($"Unknown recipe op {S(0)}."),
        };
    }
}
