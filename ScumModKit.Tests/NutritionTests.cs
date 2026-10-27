// Food resource edits on the installed game (see GameFixture): an own resource for a new dish (the dish's resource
// BP_Dish_X cloned in the same clone group), setNumber (written and added fields), setSubstances (replace and remove),
// the resource name, and ExhaustionBonus* numbers of a recipe. The built packages are read back with CUE4Parse.
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;
using ScumModKit.Build;
using ScumModKit.Package;
using ScumModKit.Pak;
using ScumModKit.Registry;

namespace ScumModKit.Tests;

public class NutritionTests
{
    private const string Dishes = "/Game/ConZ_Files/Items/Food/CookedFood", Resources = "/Game/ConZ_Files/GameResources/Food/Solids",
        Recipes = "/Game/ConZ_Files/Cooking/Recipes", Fs = "/Game/ConZ_Files/Characters/Prisoner/Blueprints/Metabolism/ForeignSubstances";

    [Fact]
    public void Own_resource_numbers_substances_and_exhaustion_bonus()
    {
        if (GameFixture.TryGame() is null) return;
        var dir = Path.Combine(Path.GetTempPath(), $"modkit-nutri-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var res = $"{Resources}/CookedDishes/BP_Dish_TestNutri";
            File.WriteAllText(Path.Combine(dir, "modkit.json"), $$"""
                { "stage": "stage", "pak": "pakchunk950-NutriTest_P",
                  "clones": [
                    {"packages": [
                      {"old": "{{Dishes}}/Dish_Salad_Fruit", "new": "{{Dishes}}/Dish_TestNutri"},
                      {"old": "{{Dishes}}/Dish_Salad_Fruit_ES", "new": "{{Dishes}}/Dish_TestNutri_ES"},
                      {"old": "{{Resources}}/CookedDishes/BP_Dish_Salad_Fruit", "new": "{{res}}"}]},
                    {"template": "{{Recipes}}/Cook_Dish_Salad_Tomato", "new": "{{Recipes}}/Cook_Dish_TestNutri"}],
                  "cooking": {"recipes": {"Cook_Dish_TestNutri": [
                    ["set", "ExhaustionBonusPerMainIngredient", 7.0],
                    ["set", "ExhaustionBonusPerHalfHourOfCooking", 2.5]]} },
                  "edits": [
                    {"op": "setNumber", "package": "{{res}}", "export": "GameResourceConsumptionData_0", "property": "TotalFat", "value": 62.5},
                    {"op": "setNumber", "package": "{{res}}", "export": "GameResourceConsumptionData_0", "property": "Alcohol", "value": 43.75},
                    {"op": "setSubstances", "package": "{{res}}", "export": "GameResourceConsumptionData_0",
                     "substances": [{"class": "{{Fs}}/Psilocybin_FS.Psilocybin_FS_C", "amount": 0.02}, {"class": "{{Fs}}/Caffeine_FS.Caffeine_FS_C", "amount": 0.1}]},
                    {"op": "setText", "package": "{{res}}", "export": "Default__BP_Dish_TestNutri_C", "property": "ResourceName", "text": "Test Nutri"},
                    {"op": "setSubstances", "package": "{{Resources}}/PsilocybeCyanescens", "export": "GameResourceConsumptionData_0", "substances": []}]
                }
                """);
            var (_, packed) = ModBuilder.Build(new BuildOptions
            {
                Project = Path.Combine(dir, "modkit.json"), OutputPak = Path.Combine(dir, "t.pak"),
                Paks = GameFixture.GameFolder!, KeyFile = Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE"),
            });
            Assert.NotNull(packed);
            var stage = Path.Combine(dir, "stage");

            // the cloned dish points at the cloned resource, the stock resource is not referenced any more
            var dish = CookedPackage.Load(Path.Combine(stage, GameSource.PakPath($"{Dishes}/Dish_TestNutri") + ".uasset"));
            var refs = Enumerable.Range(1, dish.Imports.Count).Select(i => dish.ObjectPath(-i)).ToList();
            Assert.Contains($"{res}.BP_Dish_TestNutri_C", refs);
            Assert.Contains($"{res}.Default__BP_Dish_TestNutri_C.GameResourceConsumptionData_0", refs);
            Assert.DoesNotContain(refs, r => r.Contains("BP_Dish_Salad_Fruit", StringComparison.Ordinal));

            // the registry of the pak has records for the cloned resource
            var pak = PakIndex.Read(Path.Combine(dir, "t.pak"));
            var entry = pak.Entries.Single(x => x.Path == "SCUM/AssetRegistry.bin");
            var reg = AssetRegistry.Load(File.ReadAllBytes(Path.Combine(dir, "t.pak")).AsSpan((int)entry.Offset + 53, (int)entry.Size).ToArray());
            Assert.Contains(reg.Assets, a => reg.NameText(a.ObjectPath) == $"{res}.BP_Dish_TestNutri");

            using var built = new DefaultFileProvider(stage, SearchOption.AllDirectories, new VersionContainer(EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
            built.Initialize();
            UObject Export(string package, string name) =>
                built.LoadPackage(built.Files.Keys.First(k => k.EndsWith(GameSource.PakPath(package)["SCUM/".Length..] + ".uasset", StringComparison.OrdinalIgnoreCase)))
                    .GetExports().First(o => o.Name == name);

            var data = Export(res, "GameResourceConsumptionData_0");
            Assert.Equal(62.5f, data.GetOrDefault<float>("TotalFat"));
            Assert.Equal(43.75f, data.GetOrDefault<float>("Alcohol"));
            Assert.Equal(72f, data.GetOrDefault<float>("Water")); // untouched values stay
            var subs = data.GetOrDefault<FStructFallback[]>("ForeignSubstances");
            Assert.Equal(["Psilocybin_FS_C", "Caffeine_FS_C"], subs.Select(s => s.GetOrDefault<FPackageIndex>("Class").Name));
            Assert.Equal([0.02f, 0.1f], subs.Select(s => s.GetOrDefault<float>("Amount")));
            Assert.Null(Export($"{Resources}/PsilocybeCyanescens", "GameResourceConsumptionData_0").Properties
                .FirstOrDefault(p => p.Name.Text == "ForeignSubstances"));

            var recipe = Export($"{Recipes}/Cook_Dish_TestNutri", "Cook_Dish_TestNutri");
            Assert.Equal(7f, recipe.GetOrDefault<float>("ExhaustionBonusPerMainIngredient"));
            Assert.Equal(2.5f, recipe.GetOrDefault<float>("ExhaustionBonusPerHalfHourOfCooking"));
            Assert.Equal("FloatProperty", recipe.Properties.First(p => p.Name.Text == "ExhaustionBonusPerHalfHourOfCooking").PropertyType.Text);

            var name = Export(res, "Default__BP_Dish_TestNutri_C").Properties.First(p => p.Name.Text == "ResourceName");
            Assert.Contains("Test Nutri", name.Tag?.GenericValue?.ToString());

            foreach (var f in Directory.EnumerateFiles(stage, "*.uasset", SearchOption.AllDirectories))
            {
                var (a, b) = CookedPackage.Load(f).Save();
                Assert.Equal(File.ReadAllBytes(f), a);
                Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(f, ".uexp")), b);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Food_over_100_g_per_100_g_is_reported(bool strict)
    {
        if (GameFixture.TryGame() is null) return;
        var dir = Path.Combine(Path.GetTempPath(), $"modkit-mass-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var res = $"{Resources}/CookedDishes/BP_Dish_Salad_Fruit";
            File.WriteAllText(Path.Combine(dir, "modkit.json"), $$"""
                { "stage": "stage", "edits": [
                    {"op": "setNumber", "package": "{{res}}", "export": "GameResourceConsumptionData_0", "property": "TotalFat", "value": 100},
                    {"op": "setNumber", "package": "{{res}}", "export": "GameResourceConsumptionData_0", "property": "Alcohol", "value": 70}] }
                """);
            var options = new BuildOptions
            {
                Project = Path.Combine(dir, "modkit.json"), OutputPak = Path.Combine(dir, "t.pak"), StrictNutrition = strict,
                Paks = GameFixture.GameFolder!, KeyFile = Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE"),
            };
            if (strict)
            {
                Assert.Contains("cannot be eaten", Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(options)).Message);
                return;
            }
            var (_, r) = ModBuilder.Build(options);
            Assert.Contains(r!.Warnings, w => w.Contains("BP_Dish_Salad_Fruit") && w.Contains("cannot be eaten"));
            // the stock resource is within the limit
            var stock = GameFixture.TryGame()!.Extract(res);
            var pkg = CookedPackage.Load(stock.Single(f => f.Path.EndsWith(".uasset")).Data, stock.Single(f => f.Path.EndsWith(".uexp")).Data);
            Assert.All(Build.Ops.NutritionCheck.Masses(pkg, res), m => Assert.True(m.Grams <= Build.Ops.NutritionCheck.Limit));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SetNumber_keeps_the_property_type()
    {
        if (GameFixture.TryGame() is not { } game) return;
        var files = game.Extract("/Game/ConZ_Files/Cooking/Recipes/Cook_Dish_Salad_Tomato");
        var pkg = CookedPackage.Load(files.Single(f => f.Path.EndsWith(".uasset")).Data, files.Single(f => f.Path.EndsWith(".uexp")).Data);
        var ep = Properties.ExportProperties.Read(pkg, "Cook_Dish_Salad_Tomato");
        Build.Ops.EditList.SetNumber(pkg, ep.Properties, "CookProgressMeshMaterialSlot", 2);           // IntProperty
        Build.Ops.EditList.SetNumber(pkg, ep.Properties, "CookingTemperature", 95.5);                  // FloatProperty
        Assert.Equal(2, BitConverter.ToInt32(ep.Properties.Get("CookProgressMeshMaterialSlot").Value));
        Assert.Equal(95.5f, BitConverter.ToSingle(ep.Properties.Get("CookingTemperature").Value));
        Assert.Throws<ArgumentException>(() => Build.Ops.EditList.SetNumber(pkg, ep.Properties, "CookProgressMeshMaterialSlot", 1.5));
        Assert.Throws<InvalidOperationException>(() => Build.Ops.EditList.SetNumber(pkg, ep.Properties, "RecipeName", 1));
    }
}
