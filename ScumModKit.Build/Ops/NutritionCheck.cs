// The composition of a food resource per 100 g (GameResourceConsumptionData): Water + Alcohol + TotalCarbohydrate +
// TotalFat + Protein (Fiber, Sugars and Starch are part of the carbohydrates). Found in game (2026-10-27): food whose
// composition is far over 100 g per 100 g (a salad at ~260 g) cannot be eaten, at 100 g it can. Stock resources reach
// 100.32 g (energy drink) and are edible, so the build warns above 100.5 g; the exact limit is not known.
using ScumModKit.Package;
using ScumModKit.Properties;

namespace ScumModKit.Build.Ops;

public sealed record NutritionMass(string Package, string Export, double Grams);

public static class NutritionCheck
{
    public const double Limit = 100.5;
    public static readonly string[] Parts = ["Water", "Alcohol", "TotalCarbohydrate", "TotalFat", "Protein"];

    /// <summary>Grams per 100 g of every GameResourceConsumptionData export of the package (fields not written count 0).</summary>
    public static IEnumerable<NutritionMass> Masses(CookedPackage pkg, string package)
    {
        for (var i = 0; i < pkg.Exports.Count; i++)
        {
            var e = pkg.Exports[i];
            if (e.ClassIndex >= 0 || pkg.NameText(pkg.Imports[-e.ClassIndex - 1].ObjectName) != "GameResourceConsumptionData") continue;
            var props = ExportProperties.Read(pkg, e).Properties;
            var sum = Parts.Sum(n => props.Find(n) is { Type: "FloatProperty" } p ? BitConverter.ToSingle(p.Value) : 0.0);
            yield return new NutritionMass(package, pkg.ObjectPath(i + 1), sum);
        }
    }

    /// <summary>Food resources in the stage whose composition is over 100 g per 100 g (they cannot be eaten in game).</summary>
    public static List<NutritionMass> OverLimit(string stage)
    {
        var content = Path.Combine(stage, "SCUM", "Content");
        if (!Directory.Exists(content)) return [];
        var r = new List<NutritionMass>();
        foreach (var f in Directory.EnumerateFiles(content, "*.uasset", SearchOption.AllDirectories))
        {
            // a cheap test first: only packages that name the class
            var bytes = File.ReadAllBytes(f);
            if (bytes.AsSpan().IndexOf("GameResourceConsumptionData"u8) < 0) continue;
            var package = "/Game/" + Path.GetRelativePath(content, f).Replace('\\', '/')[..^".uasset".Length];
            r.AddRange(Masses(CookedPackage.Load(f), package).Where(m => m.Grams > Limit));
        }
        return r;
    }
}
