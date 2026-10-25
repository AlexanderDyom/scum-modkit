// copyProperties on the installed game (see GameFixture): a dish model (StaticMesh + OverrideMaterials, new imports)
// and the look in the cookware (CookProgress*: soft references, int, bool, Transform struct; removed when the source
// has none). The built packages are read back with CUE4Parse and compared with the source packages.
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using ScumModKit.Build;

namespace ScumModKit.Tests;

public class CopyPropertiesTests
{
    private const string Dishes = "/Game/ConZ_Files/Items/Food/CookedFood", Recipes = "/Game/ConZ_Files/Cooking/Recipes";

    private static readonly string[] Look =
        ["CookProgressMesh", "CookProgressMeshMaterial", "CookProgressMeshMaterialSlot", "CookProgressMeshRelativeTransform", "UseMeshRelativeTransform"];

    [Fact]
    public void Dish_model_and_cookware_look_are_copied()
    {
        if (GameFixture.TryGame() is null) return;
        var dir = Path.Combine(Path.GetTempPath(), $"modkit-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var look = string.Join(",", Look.Select(p => $"\"{p}\""));
            File.WriteAllText(Path.Combine(dir, "modkit.json"), $$"""
                { "stage": "stage",
                  "clones": [
                    {"template": "{{Dishes}}/Dish_Salad_Tomato", "new": "{{Dishes}}/Dish_TestSoup"},
                    {"template": "{{Dishes}}/Dish_Salad_Tomato", "new": "{{Dishes}}/Dish_TestBowl"},
                    {"template": "{{Recipes}}/Cook_Dish_Salad_Tomato", "new": "{{Recipes}}/Cook_Dish_TestA"},
                    {"template": "{{Recipes}}/Cook_Dish_Grilled_Burger", "new": "{{Recipes}}/Cook_Dish_TestB"}],
                  "edits": [
                    {"op": "copyProperties", "package": "{{Dishes}}/Dish_TestSoup", "export": "ItemMesh0", "from": "{{Dishes}}/Dish_Soup_Chicken", "properties": ["StaticMesh", "OverrideMaterials"]},
                    {"op": "copyProperties", "package": "{{Dishes}}/Dish_TestBowl", "export": "ItemMesh0", "from": "{{Dishes}}/Dish_Casserole_Moussaka", "properties": ["StaticMesh", "OverrideMaterials"]},
                    {"op": "copyProperties", "package": "{{Recipes}}/Cook_Dish_TestA", "export": "Cook_Dish_TestA", "from": "{{Recipes}}/Cook_Dish_Grilled_Burger", "fromExport": "Cook_Dish_Grilled_Burger", "properties": [{{look}}]},
                    {"op": "copyProperties", "package": "{{Recipes}}/Cook_Dish_TestB", "export": "Cook_Dish_TestB", "from": "{{Recipes}}/Cook_Dish_Salad_Tomato", "fromExport": "Cook_Dish_Salad_Tomato", "properties": [{{look}}]}]
                }
                """);
            ModBuilder.Build(new BuildOptions
            {
                Project = Path.Combine(dir, "modkit.json"), NoPack = true,
                Paks = GameFixture.GameFolder!, KeyFile = Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE"),
            });

            using var built = new DefaultFileProvider(Path.Combine(dir, "stage"), SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
            built.Initialize();
            var game = GameFixture.Provider()!;

            UObject Export(IFileProvider p, string package, string name) =>
                p.LoadPackage(p.Files.Keys.First(k => k.EndsWith(GameSource.PakPath(package)["SCUM/".Length..] + ".uasset", StringComparison.OrdinalIgnoreCase)))
                    .GetExports().First(o => o.Name == name);

            foreach (var (clone, source) in new[] { ("Dish_TestSoup", "Dish_Soup_Chicken"), ("Dish_TestBowl", "Dish_Casserole_Moussaka") })
            {
                var c = Export(built, $"{Dishes}/{clone}", "ItemMesh0");
                var s = Export(game, $"{Dishes}/{source}", "ItemMesh0");
                Assert.Equal(RefPath(s.GetOrDefault<FPackageIndex>("StaticMesh")), RefPath(c.GetOrDefault<FPackageIndex>("StaticMesh")));
                var sm = s.GetOrDefault<FPackageIndex[]>("OverrideMaterials") ?? [];
                var cm = c.GetOrDefault<FPackageIndex[]>("OverrideMaterials") ?? [];
                Assert.NotEmpty(sm);
                Assert.Equal(sm.Select(RefPath), cm.Select(RefPath)); // a null element stays null
            }

            foreach (var (clone, source) in new[] { ("Cook_Dish_TestA", "Cook_Dish_Grilled_Burger"), ("Cook_Dish_TestB", "Cook_Dish_Salad_Tomato") })
            {
                var c = Export(built, $"{Recipes}/{clone}", clone);
                var s = Export(game, $"{Recipes}/{source}", source);
                foreach (var name in Look)
                {
                    var sp = s.Properties.FirstOrDefault(p => p.Name.Text == name);
                    var cp = c.Properties.FirstOrDefault(p => p.Name.Text == name);
                    Assert.Equal(sp is null, cp is null);
                    if (sp != null) Assert.Equal(sp.Tag?.GenericValue?.ToString(), cp!.Tag?.GenericValue?.ToString());
                }
            }

            foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "stage"), "*.uasset", SearchOption.AllDirectories))
            {
                var pkg = Package.CookedPackage.Load(f);
                var (a, b) = pkg.Save();
                Assert.Equal(File.ReadAllBytes(f), a);
                Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(f, ".uexp")), b);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Object path of a reference, /Game form (a resolved game object reports SCUM/Content/...).</summary>
    private static string RefPath(FPackageIndex? i)
    {
        if (i is null || i.IsNull) return "null";
        var p = i.ResolvedObject?.GetPathName() ?? i.Name;
        return p.StartsWith("SCUM/Content/", StringComparison.OrdinalIgnoreCase) ? "/Game/" + p["SCUM/Content/".Length..] : p;
    }
}
