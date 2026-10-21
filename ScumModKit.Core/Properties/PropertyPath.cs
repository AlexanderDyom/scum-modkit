// Helpers for properties found by a dotted path ("_itemLocation.MilitaryBasic") and for natively serialized
// FGameplayTagContainer values (StructProperty: int32 count + one FName per tag).
using System.Buffers.Binary;
using ScumModKit.Package;

namespace ScumModKit.Properties;

public static class PropertyPath
{
    /// <summary>
    /// The property at <paramref name="path"/> (names separated by '.', each one but the last a tagged struct);
    /// null when the last name is not written (the asset keeps the default). Missing structs are errors.
    /// </summary>
    public static Property? Find(CookedPackage pkg, PropertyList props, string path, out PropertyList parent)
    {
        var parts = path.Split('.');
        parent = props;
        for (var i = 0; i < parts.Length - 1; i++)
            parent = (parent.Find(parts[i]) ?? throw new KeyNotFoundException($"No property {string.Join('.', parts[..(i + 1)])}.")).OpenStruct(pkg);
        return parent.Find(parts[^1]);
    }

    /// <summary>Sets a BoolProperty at the path; a field that is not written yet gets a new tag.</summary>
    public static void SetBool(CookedPackage pkg, PropertyList props, string path, bool value)
    {
        var p = Find(pkg, props, path, out var parent);
        if (p == null)
        {
            var name = path.Split('.')[^1];
            // tag: Name, Type, Size 0, ArrayIndex 0, the value, HasPropertyGuid 0
            var h = new byte[26];
            BinaryPrimitives.WriteInt32LittleEndian(h, pkg.AddName(name));
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), pkg.AddName("BoolProperty"));
            p = new Property { Header = h, Name = name, Type = "BoolProperty" };
            parent.Add(p);
        }
        else if (p.Type != "BoolProperty") throw new InvalidOperationException($"{path} is a {p.Type}, not a BoolProperty.");
        p.Bool = value;
    }

    /// <summary>The tags of a GameplayTagContainer value.</summary>
    public static List<string> ReadTags(CookedPackage pkg, Property p)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(p.Value);
        if (p.Value.Length != 4 + (8 * count)) throw new FormatException($"{p.Name} is not a GameplayTagContainer.");
        return Enumerable.Range(0, count)
            .Select(i => pkg.NameText(new FName(BinaryPrimitives.ReadInt32LittleEndian(p.Value.AsSpan(4 + (8 * i))),
                BinaryPrimitives.ReadInt32LittleEndian(p.Value.AsSpan(8 + (8 * i)))))).ToList();
    }

    /// <summary>Replaces the tags of a GameplayTagContainer value (tag names are added to the name map).</summary>
    public static void WriteTags(CookedPackage pkg, Property p, IReadOnlyList<string> tags)
    {
        if (p.Type != "StructProperty") throw new InvalidOperationException($"{p.Name} is a {p.Type}, not a GameplayTagContainer.");
        ReadTags(pkg, p); // checks the shape
        var v = new byte[4 + (8 * tags.Count)];
        BinaryPrimitives.WriteInt32LittleEndian(v, tags.Count);
        for (var i = 0; i < tags.Count; i++) BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4 + (8 * i)), pkg.AddName(tags[i]));
        p.Struct = null;
        p.Value = v;
    }

    /// <summary>A GameplayTagContainer as the asset registry's tag value shows it: (GameplayTags=((TagName="A"),...)).</summary>
    public static string RegistryText(IEnumerable<string> tags)
    {
        var list = tags.ToList();
        return list.Count == 0 ? "(GameplayTags=)" : "(GameplayTags=(" + string.Join(",", list.Select(t => $"(TagName=\"{t}\")")) + "))";
    }
}
