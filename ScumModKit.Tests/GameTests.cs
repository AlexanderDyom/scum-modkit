// Tests on the installed game (see GameFixture): they return without checking anything when it is not available.
using ScumModKit.Build;
using ScumModKit.Clone;
using ScumModKit.Hashing;
using ScumModKit.Package;
using ScumModKit.Properties;
using ScumModKit.Registry;

namespace ScumModKit.Tests;

public class GameTests
{
    [Fact]
    public void Game_registry_saves_byte_identical_and_every_name_hash_matches()
    {
        if (GameFixture.TryGame() is not { } game) return;
        var bytes = game.ReadRegistryBytes();
        var reg = AssetRegistry.Load(bytes);
        Assert.Equal(bytes, reg.Save());
        for (var i = 0; i < reg.Names.Count; i++)
            Assert.Equal(reg.NameHashes[i], CityHash.NameHash(reg.Names[i], reg.NameWide[i]));
    }

    [Theory]
    [InlineData("/Game/ConZ_Files/Cooking/Recipes/Cook_Dish_Salad_Tomato")]
    [InlineData("/Game/ConZ_Files/Items/Books/Cookbooks/Book_Cooking_Pizza")]
    [InlineData("/Game/ConZ_Files/Economy/Table_TradeableDesc")]
    public void Game_packages_save_byte_identical(string package)
    {
        if (GameFixture.TryGame() is not { } game) return;
        var files = game.Extract(package);
        var uasset = files.Single(f => f.Path.EndsWith(".uasset", StringComparison.Ordinal)).Data;
        var uexp = files.Single(f => f.Path.EndsWith(".uexp", StringComparison.Ordinal)).Data;
        var pkg = CookedPackage.Load(uasset, uexp);
        var (a, b) = pkg.Save();
        Assert.Equal(uasset, a);
        Assert.Equal(uexp, b);
        Assert.All(pkg.Names, n => Assert.Equal(NameHashes.StrCrc32(n.Text), n.CaseHash));
    }

    [Fact]
    public void Recipe_properties_parse_and_reserialize()
    {
        if (GameFixture.TryGame() is not { } game) return;
        var files = game.Extract("/Game/ConZ_Files/Cooking/Recipes/Cook_Dish_Salad_Tomato");
        var pkg = CookedPackage.Load(files.Single(f => f.Path.EndsWith(".uasset")).Data, files.Single(f => f.Path.EndsWith(".uexp")).Data);
        var ep = ExportProperties.Read(pkg, "Cook_Dish_Salad_Tomato");
        var before = ep.Export.Data;
        Assert.NotEmpty(ep.Properties.Get("MainIngredients").OpenArray(pkg).Elements);
        ep.Save();
        Assert.Equal(before, ep.Export.Data);
    }

    [Fact]
    public void Cloned_package_gets_the_new_names()
    {
        if (GameFixture.TryGame() is not { } game) return;
        var p = new PackageRename("/Game/ConZ_Files/Items/Food/CookedFood/Dish_Salad_Tomato", "/Game/ConZ_Files/Items/Food/CookedFood/Dish_Test_Clone");
        var files = PackageCloner.Clone(game, p, CloneRenamer.For([p]));
        var pkg = CookedPackage.Load(files.Single(f => f.Path.EndsWith(".uasset")).Data, files.Single(f => f.Path.EndsWith(".uexp")).Data);
        Assert.Contains(pkg.Names, n => n.Text == "Default__Dish_Test_Clone_C");
        Assert.DoesNotContain(pkg.Names, n => n.Text == "Dish_Salad_Tomato_C");
        Assert.Contains(pkg.Names, n => n.Text.EndsWith("/BP_Dish_Salad_Tomato", StringComparison.Ordinal)); // other assets stay
    }
}
