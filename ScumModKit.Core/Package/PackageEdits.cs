// Edits of cooked packages used by crafting/recipe mods: name map renames, new imports, ingredient tags and ingredient
// slots. Ported from Chest_Mod/tools/rename_names.py with the same results byte for byte (same name and import order).
using System.Buffers.Binary;
using ScumModKit.Properties;

namespace ScumModKit.Package;

public static class PackageEdits
{
    /// <summary>
    /// Renames name map entries: <paramref name="exact"/> matches a whole entry; then every (old, new) of
    /// <paramref name="subs"/> replaces substrings, in order. <paramref name="add"/> appends entries that are not there
    /// yet. Unless <paramref name="optional"/>, every exact name and every substring must hit. Returns the number of
    /// changed entries.
    /// </summary>
    public static int RenameNames(CookedPackage pkg, IReadOnlyDictionary<string, string>? exact = null,
        IReadOnlyList<(string Old, string New)>? subs = null, IEnumerable<string>? add = null, bool optional = false)
    {
        exact ??= new Dictionary<string, string>();
        subs ??= [];
        var done = new HashSet<string>();
        var hits = new HashSet<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = 0;
        for (var i = 0; i < pkg.Names.Count; i++)
        {
            var orig = pkg.Names[i].Text;
            var s = orig;
            if (exact.TryGetValue(s, out var to)) { done.Add(s); s = to; }
            foreach (var (o, n) in subs)
                if (s.Contains(o, StringComparison.Ordinal)) { hits.Add(o); s = s.Replace(o, n, StringComparison.Ordinal); }
            if (s != orig) { pkg.SetName(i, s); changed++; }
            if (!seen.Add(s)) throw new InvalidOperationException($"Renaming makes the name {s} twice.");
        }
        foreach (var s in add ?? [])
        {
            if (!seen.Add(s)) continue;
            pkg.Names.Add(CookedPackage.NewEntry(s));
            changed++;
        }
        var missing = exact.Keys.Where(k => !done.Contains(k)).Concat(subs.Select(x => x.Old).Where(o => !hits.Contains(o))).ToList();
        if (missing.Count > 0 && !optional) throw new KeyNotFoundException($"Names not found: {string.Join(", ", missing.Order(StringComparer.Ordinal))}");
        return changed;
    }

    private static FName N(CookedPackage pkg, string name)
    {
        var i = pkg.FindName(name);
        return i >= 0 ? new FName(i, 0) : throw new KeyNotFoundException($"No name {name}.");
    }

    /// <summary>
    /// Imports <paramref name="obj"/> (class <paramref name="classPackage"/>.<paramref name="className"/>) of package
    /// <paramref name="package"/> unless it is imported already; returns its package index.
    /// </summary>
    public static int AddImport(CookedPackage pkg, string package, string obj, string classPackage, string className)
    {
        RenameNames(pkg, add: [package, obj, classPackage, className, "/Script/CoreUObject", "Package"]);
        var objName = pkg.FindName(obj);
        var cls = pkg.FindName(className);
        for (var i = 0; i < pkg.Imports.Count; i++)
            if (pkg.Imports[i].ObjectName.Index == objName && pkg.Imports[i].ClassName.Index == cls) return -(i + 1);
        return AppendImport(pkg, package, obj, classPackage, className);
    }

    /// <summary>Appends Package(package) and obj as two imports (no check for an existing one); returns obj's index.</summary>
    private static int AppendImport(CookedPackage pkg, string package, string obj, string classPackage, string className)
    {
        var count = pkg.Imports.Count;
        pkg.Imports.Add(new ObjectImport { ClassPackage = N(pkg, "/Script/CoreUObject"), ClassName = N(pkg, "Package"), OuterIndex = 0, ObjectName = N(pkg, package) });
        pkg.Imports.Add(new ObjectImport { ClassPackage = N(pkg, classPackage), ClassName = N(pkg, className), OuterIndex = -(count + 1), ObjectName = N(pkg, obj) });
        return -(count + 2);
    }

    /// <summary>
    /// An item entity setup (&lt;Item&gt;_ES) gets one more crafting ingredient tag:
    /// Default__*.CraftingIngredientInstances += {Type: tag, Amount: amount}; the array is created when missing.
    /// </summary>
    public static void AddIngredientTag(CookedPackage pkg, string tagPackage, string tagName, float amount = 1f)
    {
        RenameNames(pkg, add: ["CraftingIngredientInstances", "ArrayProperty", "StructProperty", "CraftingIngredientInstance", "Type",
            "ObjectProperty", "Amount", "FloatProperty", "None", tagPackage, tagName, "/Script/SCUM", "CraftingIngredientTag",
            "/Script/CoreUObject", "Package"]);
        var tag = AppendImport(pkg, tagPackage, tagName, "/Script/SCUM", "CraftingIngredientTag");

        var defaults = pkg.Exports.Where(e => pkg.NameText(e.ObjectName).StartsWith("Default__", StringComparison.Ordinal)).ToList();
        if (defaults.Count != 1) throw new InvalidOperationException($"Expected one Default__ export, found {defaults.Count}.");
        var ep = ExportProperties.Read(pkg, defaults[0]);
        var element = new PropertyList
        {
            Property.Simple(pkg, "Type", "ObjectProperty", BitConverter.GetBytes(tag)),
            Property.Simple(pkg, "Amount", "FloatProperty", BitConverter.GetBytes(amount)),
        };
        var arr = ep.Properties.Find("CraftingIngredientInstances");
        if (arr != null) arr.OpenArray(pkg).Elements.Add(element);
        else
        {
            // ArrayProperty tag: Name, Type, Size, ArrayIndex, InnerType, HasGuid; inner tag: StructProperty of
            // CraftingIngredientInstance (Size = elements' bytes, zero guid)
            var header = new byte[33];
            Write(header, 0, N(pkg, "CraftingIngredientInstances"));
            Write(header, 8, N(pkg, "ArrayProperty"));
            Write(header, 24, N(pkg, "StructProperty"));
            var inner = new byte[49];
            Write(inner, 0, N(pkg, "CraftingIngredientInstances"));
            Write(inner, 8, N(pkg, "StructProperty"));
            Write(inner, 24, N(pkg, "CraftingIngredientInstance"));
            ep.Properties.Add(new Property
            {
                Header = header, Name = "CraftingIngredientInstances", Type = "ArrayProperty",
                Array = new PropertyArray { Inner = inner, Elements = [element] },
            });
        }
        ep.Save();
    }

    /// <summary>
    /// Crafting recipe: Ingredients += a copy of slot 0 that allows only the ingredient tag (or item)
    /// <paramref name="obj"/> of <paramref name="package"/>, Amount = amount at every skill level, AdditionalAmount = 0.
    /// </summary>
    public static void AddIngredientSlot(CookedPackage pkg, string recipeExport, string package, string obj, int amount)
    {
        var imp = AddImport(pkg, package, obj, "/Script/SCUM", "CraftingIngredientTag");
        var ep = ExportProperties.Read(pkg, recipeExport);
        var slots = ep.Properties.Get("Ingredients").OpenArray(pkg).Elements;
        var slot = slots[0].Clone();
        var allowed = slot.Find("AllowedTypes");
        if (allowed != null) allowed.Value = [.. BitConverter.GetBytes(1), .. BitConverter.GetBytes(imp)];
        foreach (var p in slot)
            if (p.Name == "Amount") SetInts(pkg, p, amount);
            else if (p.Name == "AdditionalAmount") SetInts(pkg, p, 0);
        slots.Add(slot);
        ep.Save();
    }

    /// <summary>Ingredients[slot].Amount = amount at every skill level.</summary>
    public static void SetSlotAmount(CookedPackage pkg, string recipeExport, int slot, int amount)
    {
        var ep = ExportProperties.Read(pkg, recipeExport);
        var el = ep.Properties.Get("Ingredients").OpenArray(pkg).Elements[slot];
        foreach (var p in el.Where(p => p.Name == "Amount")) SetInts(pkg, p, amount);
        ep.Save();
    }

    /// <summary>Every top-level ObjectProperty <paramref name="property"/> of the export = <paramref name="index"/> (0 = None).</summary>
    public static int SetObjectProperty(CookedPackage pkg, string export, string property, int index)
    {
        var ep = ExportProperties.Read(pkg, export);
        var n = 0;
        foreach (var p in ep.Properties.Where(p => p.Name == property)) { p.ObjectIndex = index; n++; }
        ep.Save();
        return n;
    }

    private static void SetInts(CookedPackage pkg, Property p, int value)
    {
        foreach (var x in p.OpenStruct(pkg).Where(x => x.Type == "IntProperty")) BinaryPrimitives.WriteInt32LittleEndian(x.Value, value);
    }

    private static void Write(byte[] b, int at, FName n)
    {
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), n.Index);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at + 4), n.Number);
    }
}
