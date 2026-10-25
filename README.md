# ScumModKit

A mod compiler for SCUM (Unreal Engine 4.27): a .NET 8 library plus the `modkit` command line. It turns a short
project file (`modkit.json`) into a ready mod pak: it clones items and recipes from the installed game, edits cooked
packages, writes the asset registry, and packs everything. It needs no Unreal Editor and no other modding studio.

It is the shared base of the author's SCUM tools: the first is SCUM Recipe Editor, which links it as a git submodule.

## What it does

- **Read the game.** It opens the game's encrypted paks with your own AES key (CUE4Parse) and extracts packages,
  config files and the asset registry.
- **Clone items and recipes** under new names. The name maps are renamed, and trader rows
  (`Economy/Table_TradeableDesc`) and asset registry records are added. This is the same as ScumStudio's export, byte
  for byte.
- **Edit cooked packages**: cooking recipes (ingredients, variants, amounts, liquids, nutrition, cookware, texts,
  result), cookbooks, the recipe registry, recipes known by default, item texts, gameplay tags and flags, loot tree
  nodes, name map renames, ingredient tags and crafting slots, localization (Base FTexts + `Game.locres`).
- **Pack.** It writes a pak v11 that is byte-identical to `repak pack --version V11 -m ../../../` for the same data
  order, plus a `.sig` and a full `SCUM/AssetRegistry.bin` with the mod's new records.
- **Inspect** packages, properties, DataTables, registries and gameplay tags.

Every cooked package and registry it loads saves byte-identical when nothing is changed. The tests check this on the
installed game.

## Quick start

```
dotnet build ScumModKit.sln -c Release
set SCUM_AES_KEY_FILE=C:\path\outside\the\repo\key.txt
ScumModKit.Cli\bin\Release\net8.0\modkit.exe build MyMod\modkit.json
```

The game folder comes from `--paks`, env `SCUM_GAME`, or the SCUM install found in the Steam libraries. The key comes
from `--key`, env `SCUM_AES_KEY_FILE`, or a `key.txt` next to the project. The key is never printed.

The result is `MyMod/build/<pak>.pak` + `.sig`. Copy both into `SCUM/Content/Paks/~mods`. If several mods each ship an
asset registry, merge them with [ScumRegistryMerger](https://github.com/AlexanderDyom/scum-registry-merger).
Mods are for single player or your own server; check the server's rules.

## modkit.json

```jsonc
{
  "name": "FoodMod",
  "pak": "pakchunk901-FoodMod_P",
  "stage": "build/stage",                       // must be empty: the build starts from scratch
  "clones": [
    { "template": "/Game/ConZ_Files/Items/Food/CookedFood/Dish_Salad_Tomato",
      "new": "/Game/ConZ_Files/Items/Food/CookedFood/Dish_FoodMod_CrazySalad", "with": ["_ES"] },
    { "template": "/Game/ConZ_Files/Cooking/Recipes/Cook_Dish_Salad_Tomato",
      "new": "/Game/ConZ_Files/Cooking/Recipes/Cook_Dish_FoodMod_CrazySalad" }
  ],
  "include": [ { "from": "cooked/Mods/MyMod", "to": "SCUM/Content/Mods/MyMod" } ],
  "cooking": {
    "recipes": {
      "Cook_Dish_FoodMod_CrazySalad": [
        ["text", "RecipeName", "Crazy Salad"],
        ["result", "/Game/ConZ_Files/Items/Food/CookedFood/Dish_FoodMod_CrazySalad.Dish_FoodMod_CrazySalad_C"],
        ["remove", "main", "Vinegar"],
        ["set_options", "main", "Tomato", [["/Game/ConZ_Files/Items/Food/MeatAndAnimalProducts/Egg.Egg_C", true, 1]]],
        ["liters", "main", "Oil", 0.25],
        ["set", "CookingTime", 30.0]
      ]
    },
    "newRecipes": ["Cook_Dish_FoodMod_CrazySalad"],
    "books": { "Book_Cooking_Pizza": ["Cook_Dish_FoodMod_CrazySalad"] },
    "ownBooks": {},
    "knownByDefault": [],
    "itemTexts": { "/Game/ConZ_Files/Items/Food/CookedFood/Dish_FoodMod_CrazySalad_ES": ["Crazy Salad", "Don't ask."] }
  },
  "edits": [
    { "op": "rename", "package": "/Game/...", "exact": { "SM_Old": "SM_New" } }
  ]
}
```

`modkit build` runs these steps in order: clones, the texts of a ScumStudio journal (optional `"journal"`), `include`,
`cooking`, `edits`, then pack. Game packages that an edit needs are copied from the game on first use.

Recipe ops (`cooking.recipes`): `set`, `remove`, `add_option`, `usage`, `set_options`, `add_ingredient`, `text`,
`result`, `title`, `nutrient`, `utility`, `liters`. A variant is `[item, whole, usage(, liters(, resource))]`; a liquid
variant names its game resource class, and `""` as the item means any container.
The full list with arguments is in `ScumModKit.Build/Ops/CookingEdits.cs` (`ParseOp`).

Package edits (`edits`): `extract`, `copyPackage`, `rename`, `addIngredientSlot`, `setSlotAmount`,
`addIngredientTag`, `setObject`, `setText`, `localize`, `setTags`, `setBool`, `addLootNode`, `setRegistryTag`,
`copyProperties` (values of a game package's export, e.g. a dish model or the look in the cookware; imports added). They
are documented at the top of `ScumModKit.Build/Ops/EditList.cs`.

## Commands

| command | what |
|---|---|
| `modkit build <modkit.json>` | the whole mod: clones, edits, pak + .sig + registry |
| `modkit pack [<modkit.json>] --stage <dir> --out <pak>` | pack a folder, with a registry for its clones |
| `modkit extract <path>... --stage <dir>` | copy game packages or folders out of the paks |
| `modkit clone <modkit.json>` | only the clones, into a stage |
| `modkit pkg verify\|names\|props\|table` | check and inspect cooked packages |
| `modkit reg game\|hashes\|diff\|compare\|names\|show` | inspect asset registries |
| `modkit tags [prefix] [--json]` | the game's gameplay tags (e.g. `ItemSpawnType.`, `ItemLootTreeNodes.`) |

`modkit --help` shows all options.

## Notes on SCUM

- **Item spawning uses the loot tree.** Spawner presets name nodes `ItemLootTreeNodes.<Place>...`, which are rows of
  the tag tables `Data/Tables/Items/Spawning/Nodes/ILTN_*`. A leaf's last part is an item name. A cloned item only
  spawns in the world when a leaf names it (`addLootNode`); its `_itemSpawnTypes` tags are not used by any spawner
  preset.
- **Recipe lists.** A new recipe shows up only through a cookbook or the known-by-default list, and it needs an entry
  in the recipe registry (`newRecipes`).
- **Several mods.** Every mod with new items ships a full asset registry, and only the highest-priority one counts.
  ScumRegistryMerger merges them.

Building and tests: [BUILDING.md](BUILDING.md).

## Credits

Thanks to [CUE4Parse](https://github.com/FabianFG/CUE4Parse) for reading the game's paks. The pak layout follows
[repak](https://github.com/trumank/repak). Licences: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). ScumModKit is
MIT-licensed ([LICENSE](LICENSE)).

## Disclaimer

This is an unofficial fan tool, not affiliated with Gamepires or Jagex; SCUM and its assets are their property. This
repository contains no game files and no AES key. ScumModKit reads the game installed on your own PC with the key you
provide, and the mods it builds are made there.
