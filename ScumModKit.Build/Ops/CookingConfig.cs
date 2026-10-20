// The "cooking" section of modkit.json (same content as Food_Mod/tools/food_config.py) and its application to a stage,
// in recipe_edit.py's order: recipes, item texts, registry entries, cookbooks, own cookbooks, known by default.
using System.Text.Json;
using ScumModKit.Package;

namespace ScumModKit.Build.Ops;

public sealed class CookingConfig
{
    /// <summary>Recipe package name -> ops (JSON arrays, see CookingEdits.ParseOp).</summary>
    public Dictionary<string, List<JsonElement>> Recipes { get; set; } = [];
    /// <summary>/Game path of an &lt;Item&gt;_ES package -> [caption, description?].</summary>
    public Dictionary<string, List<string>> ItemTexts { get; set; } = [];
    /// <summary>Recipes added to CookingRecipeRegistry._entries.</summary>
    public List<string> NewRecipes { get; set; } = [];
    /// <summary>Stock cookbook -> recipes it also teaches.</summary>
    public Dictionary<string, List<string>> Books { get; set; } = [];
    /// <summary>Own cookbook (a clone) -> exactly these recipes.</summary>
    public Dictionary<string, List<string>> OwnBooks { get; set; } = [];
    public List<string> KnownByDefault { get; set; } = [];

    public const string RecipeFolder = "/Game/ConZ_Files/Cooking/Recipes";
    public const string BookFolder = "/Game/ConZ_Files/Items/Books/Cookbooks";
    public const string Registry = "/Game/ConZ_Files/Cooking/Data/CookingRecipeRegistry";
    public const string Skill = "/Game/ConZ_Files/Skills/CookingSkill";

    /// <summary>The game packages these edits need in the stage (copied from the game when missing).</summary>
    public IEnumerable<string> Packages()
    {
        foreach (var r in Recipes.Keys) yield return $"{RecipeFolder}/{r}";
        foreach (var es in ItemTexts.Keys) yield return es;
        if (NewRecipes.Count > 0) yield return Registry;
        foreach (var b in Books.Keys) yield return $"{BookFolder}/{b}";
        foreach (var b in OwnBooks.Keys) yield return $"{BookFolder}/{b}";
        if (KnownByDefault.Count > 0) yield return Skill;
    }

    public void Apply(Stage stage)
    {
        foreach (var (recipe, ops) in Recipes)
            stage.Edit($"{RecipeFolder}/{recipe}", pkg => CookingEdits.ApplyRecipe(pkg, recipe, ops.Select(CookingEdits.ParseOp).ToList()));
        foreach (var (es, t) in ItemTexts)
            stage.Edit(es, pkg => CookingEdits.SetItemTexts(pkg, t[0], t.Count > 1 ? t[1] : null));
        if (NewRecipes.Count > 0) stage.Edit(Registry, pkg => CookingEdits.AddRegistryEntries(pkg, NewRecipes));
        foreach (var (book, recs) in Books) stage.Edit($"{BookFolder}/{book}", pkg => CookingEdits.AddToBook(pkg, recs));
        foreach (var (book, recs) in OwnBooks) stage.Edit($"{BookFolder}/{book}", pkg => CookingEdits.AddToBook(pkg, recs, replace: true));
        if (KnownByDefault.Count > 0) stage.Edit(Skill, pkg => CookingEdits.AddKnownByDefault(pkg, KnownByDefault));
    }
}
