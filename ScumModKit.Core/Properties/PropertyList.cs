// Tagged properties of a cooked UE 4.27 export (FPropertyTag + value, ended by a "None" name), as a small tree:
// each property keeps its tag bytes and its value bytes; arrays of structs and tagged structs are opened on demand
// (OpenArray / OpenStruct) and serialized again with every Size recomputed. Unopened values stay byte-for-byte.
// Same model as Food_Mod/tools/recipe_edit.py (parse_props / ser_props).
using System.Buffers.Binary;
using ScumModKit.Package;

namespace ScumModKit.Properties;

public sealed class Property
{
    /// <summary>Tag bytes up to the value: Name, Type, Size (at 16), ArrayIndex, type data, HasPropertyGuid (+ guid).</summary>
    public byte[] Header = [];
    public string Name = "", Type = "";
    /// <summary>Value bytes (when not opened).</summary>
    public byte[] Value = [];
    /// <summary>Opened ArrayProperty of tagged structs: element count is Elements.Count.</summary>
    public PropertyArray? Array;
    /// <summary>Opened StructProperty (tagged struct).</summary>
    public PropertyList? Struct;

    /// <summary>BoolProperty: the value lives in the tag.</summary>
    public bool Bool
    {
        get => Header[24] != 0;
        set => Header[24] = (byte)(value ? 1 : 0);
    }

    /// <summary>Size of the tag before the value (as written by FPropertyTag::SerializeTaggedProperty in 4.27).</summary>
    internal static int HeaderLength(string type, byte[] d, int at)
    {
        var h = 24;
        h += type switch
        {
            "StructProperty" => 24,                                           // StructName + StructGuid
            "ArrayProperty" or "EnumProperty" or "ByteProperty" or "SetProperty" => 8, // inner type / enum name
            "MapProperty" => 16,                                              // key + value type
            "BoolProperty" => 1,                                              // the value
            _ => 0,
        };
        var hasGuid = d[at + h];
        return h + 1 + (hasGuid != 0 ? 16 : 0);
    }

    public Property Clone() => new()
    {
        Header = (byte[])Header.Clone(), Name = Name, Type = Type, Value = (byte[])Value.Clone(),
        Array = Array?.Clone(), Struct = Struct?.Clone(),
    };

    /// <summary>Opens an ArrayProperty whose elements are tagged structs.</summary>
    public PropertyArray OpenArray(CookedPackage pkg)
    {
        if (Array != null) return Array;
        if (Type != "ArrayProperty") throw new InvalidOperationException($"{Name} is a {Type}, not an array.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(Value);
        var inner = Value.AsSpan(4, 49).ToArray(); // inner FPropertyTag of the StructProperty elements (no guid)
        if (pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(inner.AsSpan(8))].Text != "StructProperty")
            throw new NotSupportedException($"{Name}: only arrays of structs can be opened.");
        var arr = new PropertyArray { Inner = inner };
        var pos = 4 + 49;
        for (var i = 0; i < count; i++) arr.Elements.Add(PropertyList.Parse(Value, ref pos, Value.Length, pkg));
        if (pos != Value.Length) throw new FormatException($"{Name}: parsed {pos} of {Value.Length} bytes.");
        Array = arr;
        return arr;
    }

    /// <summary>Opens a StructProperty whose value is tagged properties.</summary>
    public PropertyList OpenStruct(CookedPackage pkg)
    {
        if (Struct != null) return Struct;
        if (Type != "StructProperty") throw new InvalidOperationException($"{Name} is a {Type}, not a struct.");
        var pos = 0;
        var s = PropertyList.Parse(Value, ref pos, Value.Length, pkg);
        if (pos != Value.Length) throw new FormatException($"{Name}: not a tagged struct.");
        Struct = s;
        return s;
    }

    /// <summary>ObjectProperty value: package index (0 = None, &lt;0 import, &gt;0 export).</summary>
    public int ObjectIndex
    {
        get => BinaryPrimitives.ReadInt32LittleEndian(Value);
        set => BinaryPrimitives.WriteInt32LittleEndian(Value, value);
    }

    /// <summary>NameProperty value (name map index, number).</summary>
    public FName NameValue
    {
        get => new(BinaryPrimitives.ReadInt32LittleEndian(Value), BinaryPrimitives.ReadInt32LittleEndian(Value.AsSpan(4)));
        set { BinaryPrimitives.WriteInt32LittleEndian(Value, value.Index); BinaryPrimitives.WriteInt32LittleEndian(Value.AsSpan(4), value.Number); }
    }

    internal byte[] Serialize(byte[] none)
    {
        byte[] value;
        if (Array != null)
        {
            var body = new MemoryStream();
            foreach (var el in Array.Elements) body.Write(el.Serialize(none));
            var inner = (byte[])Array.Inner.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(inner.AsSpan(16), (int)body.Length);
            value = [.. BitConverter.GetBytes(Array.Elements.Count), .. inner, .. body.ToArray()];
        }
        else if (Struct != null) value = Struct.Serialize(none);
        else value = Value;
        var header = (byte[])Header.Clone();
        if (Type != "BoolProperty") BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), value.Length);
        return [.. header, .. value];
    }

    /// <summary>A new StructProperty of tagged properties (StructName, zero struct guid, no property guid).</summary>
    public static Property NewStruct(CookedPackage pkg, string name, string structName, PropertyList value)
    {
        var h = new byte[49];
        BinaryPrimitives.WriteInt32LittleEndian(h, pkg.AddName(name));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), pkg.AddName("StructProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), pkg.AddName(structName));
        return new Property { Header = h, Name = name, Type = "StructProperty", Struct = value };
    }

    /// <summary>A new ArrayProperty of tagged structs (inner tag: StructProperty of structName).</summary>
    public static Property NewStructArray(CookedPackage pkg, string name, string structName, List<PropertyList> elements)
    {
        var h = new byte[33];
        BinaryPrimitives.WriteInt32LittleEndian(h, pkg.AddName(name));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), pkg.AddName("ArrayProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), pkg.AddName("StructProperty"));
        var inner = new byte[49];
        BinaryPrimitives.WriteInt32LittleEndian(inner, pkg.AddName(name));
        BinaryPrimitives.WriteInt32LittleEndian(inner.AsSpan(8), pkg.AddName("StructProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(inner.AsSpan(24), pkg.AddName(structName));
        return new Property { Header = h, Name = name, Type = "ArrayProperty", Array = new PropertyArray { Inner = inner, Elements = elements } };
    }

    /// <summary>A new tag (no type data, no guid) for simple types such as Float/Int/ObjectProperty.</summary>
    public static Property Simple(CookedPackage pkg, string name, string type, byte[] value)
    {
        var h = new byte[25];
        BinaryPrimitives.WriteInt32LittleEndian(h, pkg.AddName(name));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), pkg.AddName(type));
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), value.Length);
        return new Property { Header = h, Name = name, Type = type, Value = value };
    }
}

public sealed class PropertyArray
{
    public byte[] Inner = [];
    public List<PropertyList> Elements = [];

    public PropertyArray Clone() => new() { Inner = (byte[])Inner.Clone(), Elements = Elements.Select(e => e.Clone()).ToList() };
}

public sealed class PropertyList : List<Property>
{
    public Property? Find(string name) => this.FirstOrDefault(p => p.Name == name);

    public Property Get(string name) => Find(name) ?? throw new KeyNotFoundException($"No property {name}.");

    public PropertyList Clone()
    {
        var c = new PropertyList();
        c.AddRange(this.Select(p => p.Clone()));
        return c;
    }

    /// <summary>Parses tags from <paramref name="pos"/> up to and including the "None" terminator.</summary>
    public static PropertyList Parse(byte[] d, ref int pos, int end, CookedPackage pkg)
    {
        var list = new PropertyList();
        while (pos < end)
        {
            var name = pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos))].Text;
            if (name == "None") { pos += 8; return list; }
            var type = pkg.Names[BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 8))].Text;
            var size = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 16));
            var h = Property.HeaderLength(type, d, pos);
            if (type == "BoolProperty") size = 0;
            if (size < 0 || pos + h + size > end) throw new FormatException($"Property {name}: bad size {size}.");
            list.Add(new Property { Header = d[pos..(pos + h)], Name = name, Type = type, Value = d[(pos + h)..(pos + h + size)] });
            pos += h + size;
        }
        throw new FormatException("No None tag before the end of the properties.");
    }

    public byte[] Serialize(byte[] none)
    {
        var s = new MemoryStream();
        foreach (var p in this) s.Write(p.Serialize(none));
        s.Write(none);
        return s.ToArray();
    }
}

/// <summary>The tagged properties of one export plus the native bytes after them.</summary>
public sealed class ExportProperties
{
    public required CookedPackage Package { get; init; }
    public required ObjectExport Export { get; init; }
    public required PropertyList Properties { get; init; }
    public byte[] Tail { get; init; } = [];

    public static ExportProperties Read(CookedPackage pkg, ObjectExport e)
    {
        var pos = 0;
        var props = PropertyList.Parse(e.Data, ref pos, e.Data.Length, pkg);
        return new ExportProperties { Package = pkg, Export = e, Properties = props, Tail = e.Data[pos..] };
    }

    public static ExportProperties Read(CookedPackage pkg, string exportName) => Read(pkg, pkg.Export(exportName));

    /// <summary>Writes the properties back into the export's data.</summary>
    public void Save() => Export.Data = [.. Properties.Serialize(Package.NoneTag()), .. Tail];
}
