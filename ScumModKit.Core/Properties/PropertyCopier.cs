// Copies tagged properties from one cooked package into another: every name is re-added to the target's name map and
// every object reference to an import is re-created as an import of the target (with its outer chain). Values that
// hold no names or references (numbers, strings, binary structs like Vector/Quat) are copied as they are; tagged
// structs and arrays of them are walked.
using System.Buffers.Binary;
using ScumModKit.Package;

namespace ScumModKit.Properties;

public sealed class PropertyCopier(CookedPackage src, CookedPackage dst)
{
    /// <summary>Natively serialized structs without names or object references inside: copied byte for byte.</summary>
    private static readonly HashSet<string> PlainStructs =
    [
        "Vector", "Vector2D", "Vector4", "Rotator", "Quat", "LinearColor", "Color", "Guid", "IntPoint", "IntVector",
        "Box", "Box2D", "DateTime", "Timespan", "FrameNumber", "Plane", "Matrix",
    ];

    private readonly Dictionary<int, int> _imports = [];

    /// <summary>The property as a property of the target package (a new object; the source is not changed).</summary>
    public Property Copy(Property p)
    {
        var header = (byte[])p.Header.Clone();
        Name(header, 0);
        Name(header, 8);
        var structName = "";
        var inner = "";
        switch (p.Type)
        {
            case "StructProperty":
                structName = src.Names[BinaryPrimitives.ReadInt32LittleEndian(p.Header.AsSpan(24))].Text;
                Name(header, 24);
                break;
            case "ArrayProperty" or "SetProperty" or "EnumProperty" or "ByteProperty":
                inner = src.Names[BinaryPrimitives.ReadInt32LittleEndian(p.Header.AsSpan(24))].Text;
                Name(header, 24);
                break;
            case "MapProperty":
                throw new NotSupportedException($"{p.Name}: MapProperty values cannot be copied yet.");
        }

        var c = new Property { Header = header, Name = p.Name, Type = p.Type };
        switch (p.Type)
        {
            case "StructProperty":
                if (structName == "GameplayTagContainer") c.Value = TagContainer(p.Value);
                else if (structName is "SoftObjectPath" or "SoftClassPath") c.Value = NameAt(p.Value, 0);
                else if (PlainStructs.Contains(structName)) c.Value = (byte[])p.Value.Clone();
                else
                {
                    PropertyList s;
                    try { s = p.Struct ?? p.Clone().OpenStruct(src); }
                    catch (FormatException) { throw new NotSupportedException($"{p.Name}: struct {structName} is natively serialized and cannot be copied yet."); }
                    c.Struct = CopyList(s);
                }
                break;
            case "ArrayProperty" or "SetProperty":
                CopyArray(p, c, inner);
                break;
            default:
                c.Value = Scalar(p.Type, inner, p.Value, p.Name);
                break;
        }
        return c;
    }

    public PropertyList CopyList(PropertyList list)
    {
        var r = new PropertyList();
        r.AddRange(list.Select(Copy));
        return r;
    }

    private byte[] Scalar(string type, string enumName, byte[] v, string name) => type switch
    {
        "ObjectProperty" or "ClassProperty" or "WeakObjectProperty" or "InterfaceProperty" => Object(v),
        "SoftObjectProperty" or "SoftClassProperty" or "NameProperty" or "EnumProperty" => NameAt(v, 0),
        "ByteProperty" when v.Length == 8 => NameAt(v, 0),                           // enum value as a name
        "TextProperty" when v.Length > 13 && (sbyte)v[4] == 11 => NameAt(v, 5),      // string table: table id is a name
        "LazyObjectProperty" or "DelegateProperty" or "MulticastDelegateProperty" or "MulticastInlineDelegateProperty"
            or "MulticastSparseDelegateProperty" or "FieldPathProperty"
            => throw new NotSupportedException($"{name}: {type} values cannot be copied yet."),
        _ => (byte[])v.Clone(),                                                       // numbers, bool, strings, texts
    };

    private void CopyArray(Property p, Property c, string inner)
    {
        if (inner == "StructProperty")
        {
            var arr = p.Array ?? p.Clone().OpenArray(src);
            var innerTag = (byte[])arr.Inner.Clone();
            Name(innerTag, 0);
            Name(innerTag, 8);
            Name(innerTag, 24);
            c.Array = new PropertyArray { Inner = innerTag, Elements = arr.Elements.Select(CopyList).ToList() };
            return;
        }
        var v = p.Value;
        var count = BinaryPrimitives.ReadInt32LittleEndian(v);
        var w = new MemoryStream();
        w.Write(v, 0, 4);
        var at = 4;
        for (var i = 0; i < count; i++)
        {
            switch (inner)
            {
                case "ObjectProperty" or "ClassProperty" or "WeakObjectProperty" or "InterfaceProperty":
                    w.Write(Object(v[at..(at + 4)])); at += 4; break;
                case "NameProperty" or "EnumProperty":
                    w.Write(NameAt(v[at..(at + 8)], 0)); at += 8; break;
                case "SoftObjectProperty" or "SoftClassProperty":
                {
                    var len = BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(at + 8));
                    var end = at + 12 + (len >= 0 ? len : -2 * len);
                    w.Write(NameAt(v[at..end], 0)); at = end; break;
                }
                case "IntProperty" or "FloatProperty" or "UInt32Property" or "BoolProperty" or "ByteProperty" or "Int64Property"
                    or "UInt64Property" or "DoubleProperty" or "Int16Property" or "UInt16Property" or "Int8Property" or "StrProperty":
                    w.Write(v, at, v.Length - at); at = v.Length; i = count; break;          // no names inside: rest as is
                default:
                    throw new NotSupportedException($"{p.Name}: arrays of {inner} cannot be copied yet.");
            }
        }
        c.Value = w.ToArray();
    }

    private byte[] TagContainer(byte[] v)
    {
        var r = (byte[])v.Clone();
        var count = BinaryPrimitives.ReadInt32LittleEndian(v);
        for (var i = 0; i < count; i++) Name(r, 4 + (8 * i));
        return r;
    }

    private byte[] NameAt(byte[] v, int at)
    {
        var r = (byte[])v.Clone();
        Name(r, at);
        return r;
    }

    /// <summary>Rewrites the name map index at <paramref name="at"/> (number kept) to the target's index of that name.</summary>
    private void Name(byte[] b, int at)
    {
        var i = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(at), dst.AddName(src.Names[i].Text));
    }

    private byte[] Object(byte[] v) => BitConverter.GetBytes(MapIndex(BinaryPrimitives.ReadInt32LittleEndian(v)));

    /// <summary>A package index of the source as one of the target: imports are found or added (outer chain first).</summary>
    public int MapIndex(int index)
    {
        if (index == 0) return 0;
        if (index > 0)
            throw new NotSupportedException($"A reference to {src.ObjectPath(index)} (an object inside the source package) cannot be copied.");
        if (_imports.TryGetValue(index, out var known)) return known;
        var imp = src.Imports[-index - 1];
        var outer = MapIndex(imp.OuterIndex);
        FName N(FName n) => new(dst.AddName(src.Names[n.Index].Text), n.Number);
        var want = new ObjectImport { ClassPackage = N(imp.ClassPackage), ClassName = N(imp.ClassName), OuterIndex = outer, ObjectName = N(imp.ObjectName) };
        var at = dst.Imports.FindIndex(x => x.ClassPackage == want.ClassPackage && x.ClassName == want.ClassName &&
                                            x.OuterIndex == want.OuterIndex && x.ObjectName == want.ObjectName);
        if (at < 0)
        {
            dst.Imports.Add(want);
            at = dst.Imports.Count - 1;
        }
        return _imports[index] = -(at + 1);
    }
}
