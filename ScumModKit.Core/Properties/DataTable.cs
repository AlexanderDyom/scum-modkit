// A cooked UDataTable export: its own tagged properties, the object guid flag, then NumRows rows of (FName row name,
// tagged row struct). Rows are kept as property lists; Save writes the export again.
using System.Buffers.Binary;
using ScumModKit.Package;

namespace ScumModKit.Properties;

public sealed class DataTable
{
    public required ExportProperties Object { get; init; }
    /// <summary>bytes between the table's properties and the row count (UObject guid flag)</summary>
    public byte[] Guid { get; init; } = [];
    public List<(FName Name, PropertyList Row)> Rows { get; } = [];
    public byte[] Tail { get; init; } = [];

    public static DataTable Read(CookedPackage pkg, ObjectExport e)
    {
        var d = e.Data;
        var pos = 0;
        var props = PropertyList.Parse(d, ref pos, d.Length, pkg);
        if (BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos)) != 0) throw new NotSupportedException("DataTable with an object guid.");
        var guid = d[pos..(pos + 4)];
        pos += 4;
        var count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos));
        pos += 4;
        var rows = new List<(FName, PropertyList)>();
        for (var i = 0; i < count; i++)
        {
            var name = new FName(BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos)), BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos + 4)));
            pos += 8;
            rows.Add((name, PropertyList.Parse(d, ref pos, d.Length, pkg)));
        }
        var t = new DataTable
        {
            Object = new ExportProperties { Package = pkg, Export = e, Properties = props },
            Guid = guid, Tail = d[pos..],
        };
        t.Rows.AddRange(rows);
        return t;
    }

    public void Save()
    {
        var none = Object.Package.NoneTag();
        var s = new MemoryStream();
        s.Write(Object.Properties.Serialize(none));
        s.Write(Guid);
        s.Write(BitConverter.GetBytes(Rows.Count));
        foreach (var (name, row) in Rows)
        {
            s.Write(BitConverter.GetBytes(name.Index));
            s.Write(BitConverter.GetBytes(name.Number));
            s.Write(row.Serialize(none));
        }
        s.Write(Tail);
        Object.Export.Data = s.ToArray();
    }
}
