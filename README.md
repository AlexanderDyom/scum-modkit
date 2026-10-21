# ScumModKit

Our SCUM mod compiler: a .NET 8 library plus the `modkit` CLI. It builds mod paks without ScumStudio and without the
mods' Python scripts. The recipe and crafting tools are meant to embed the library. The plan is in
[ARCHITECTURE.md](ARCHITECTURE.md). Slices 1–4 are done: Food_Mod and Chest_Mod build with `modkit build` alone, and their
paks match the ScumStudio + Python builds byte for byte.

## Library map

| namespace | what |
|---|---|
| `ScumModKit.Pak` | `PakWriter` (v11, as repak writes it), `PakIndex` (reader), `PakPriority` |
| `ScumModKit.Registry` | `AssetRegistry` (v8 read/write), `RecordCloner` (registry records for clones) |
| `ScumModKit.Package` | `CookedPackage` (.uasset/.uexp model, saves byte-identical), `PackageEdits` (names, imports, ingredient tags and slots) |
| `ScumModKit.Properties` | `PropertyList` (tagged-property tree), `ExportProperties`, `DataTable` |
| `ScumModKit.Text` | FText values (invariant, Base), `Locres` v3 writer, text hashes |
| `ScumModKit.Clone` | `CloneRenamer`, `CloneGroup`: what a clone renames |
| `ScumModKit.Build` | `GameSource` (encrypted game paks), `Stage`, `PackageCloner` (+ trader rows), `ModPacker`, `ModBuilder`, `ModProject` |
| `ScumModKit.Build.Ops` | `CookingEdits` / `CookingConfig` (recipes, cookbooks, registry, known recipes), `EditList` (generic edits) |

## `modkit build`

`modkit build <modkit.json> [--stage <empty dir>] [--out <pak>]` runs these steps:

1. clones from the game (name maps renamed, plus a trader row in `Economy/Table_TradeableDesc` for every cloned item
   whose template has one);
2. the `setAssetValue` texts of the ScumStudio journal;
3. `include` files (cooked editor assets, `SCUM/Config/Tags`...);
4. the `cooking` edits;
5. the `edits` list;
6. `pack`.

Game packages that an edit needs are copied from the game into the stage on first use.

Moving a mod over: `migrate/cooking_config_to_json.py` (recipe_edit.py configs) and `migrate/record_python_edits.py`.
The second runs the mod's rename_names-based scripts with the calls recorded instead of done, and writes the "edits"
list, including a `localize` edit from a texts module.

Other commands: `modkit extract` (game files into a stage), `modkit clone`, `modkit pkg verify|names|table`.

```
dotnet build tools/modkit/ScumModKit.Cli -c Release
tools/modkit/ScumModKit.Cli/bin/Release/net8.0/modkit.exe --help
```

## `modkit pack`

Input: a stage folder (the pak's content as mounted: `SCUM/Content/...`, `SCUM/Config/...`) and the clones. The clones
come from `modkit.json`, a ScumStudio `journal.jsonl` (its `cloneAsset` edits) or `--clone`.

Output:

- **Pak v11**, mount `../../../`, uncompressed and unencrypted. The index is byte-identical to what
  `repak pack --version V11 -m ../../../` writes. repak packs in parallel, so the order of the file data in its pak
  changes from run to run. modkit writes the data sorted by path. With `--data-order-from <repak pak>` it uses that
  pak's data order, and the whole file is byte-identical.
- **`.sig`**: a copy of a stock one (`pakchunk44-WindowsNoEditor.sig`), as the mods' build scripts do.
- **`SCUM/AssetRegistry.bin`** when the mod clones packages. It is the game's full registry (read from the encrypted
  stock paks via CUE4Parse) plus records for the clones at the end. Each clone record is a copy of a template record
  with the names renamed. This is the same as ScumStudio's export, down to the byte:
  - clones are ordered by new primary path, ignoring case. Inside a clone the packages keep their given order. A
    package's records keep the template's order;
  - in every name of a record and in every tag value, template paths and short names become the new ones. This is
    one pass, longest match first, so `CR_Metal_Chest_Skin` is not hit by `CR_Metal_Chest`;
  - `PackagePath` is the new package's folder. A clone may live in another folder than its template;
  - tags, bundles, chunk ids and package flags are copied. New names go to the end of the name batch with their
    CityHash64, in ScumStudio's order (a record's tags first, then its names);
  - no records are added for a clone whose package is not in the stage, or whose new package already has records.
    Both cases print a warning or a note.

Without clones, `--registry auto` (the default) ships the stage's registry file as it is, if there is one.

```
modkit pack --stage Food_Mod/build/Client/staging --journal Food_Mod/Food_Mod.ssproj/journal.jsonl --out <tmp>/pakchunk901-FoodMod_P.pak
modkit pack food.modkit.json --out-dir <tmp>
```

The AES key comes from `--key`, `SCUM_AES_KEY_FILE`, or `key.txt` in the current folder, the stage folder or one of
their parents. It is never printed.

### modkit.json (draft)

```jsonc
{
  "name": "FoodMod",
  "pak": "pakchunk901-FoodMod_P",
  "stage": "build/Client/staging",            // relative to this file
  "journal": "Food_Mod.ssproj/journal.jsonl", // optional: ScumStudio clones too
  "clones": [
    { "template": "/Game/ConZ_Files/Items/Food/CookedFood/Dish_Salad_Tomato",
      "new": "/Game/ConZ_Files/Items/Food/CookedFood/Dish_FoodMod_CrazySalad", "with": ["_ES"] },
    { "packages": [{ "old": "/Game/ConZ_Files/Cooking/Recipes/Cook_Dish_Pizza_Neapolitan",
                     "new": "/Game/ConZ_Files/Cooking/Recipes/Cook_Dish_FoodMod_CrackerPizza" }] }
  ]
}
```

## Checked (2026-10-20)

Slices 2–4:

- `modkit extract` = `ss.sh pak unpack` (484 files);
- `pkg verify`: 1001 packages (game, ScumStudio clones, Chest_Mod's cooked editor assets) re-save byte-identical, and
  every name hash checks out;
- clones + journal texts + trader rows = the full ScumStudio export, every file (Food_Mod 18, Chest_Mod 512);
- `modkit build` of Food_Mod (cooking edits) and Chest_Mod (823 recorded edits + localization) = the ScumStudio +
  Python builds, the whole pak byte for byte.

Slice 1:

| | Food_Mod (4, then 5 clones) | Chest_Mod (51 clones) |
|---|---|---|
| registry vs ScumStudio's | byte-identical (10 / 14 records) | byte-identical (408 records) |
| pak vs the repak build, same data order | byte-identical | byte-identical (1045 files) |
| `ss.sh assetreg verify` | round trip OK, 0 hash mismatches | round trip OK, 0 hash mismatches |
| `ss.sh pak list` | reads it, same file list | reads it, same file list |
| `modkit.json` instead of the journal | byte-identical registry | — |

## Other commands

`modkit reg game|hashes|diff|compare|names|show` are tools for inspecting registries: read the game's own registry,
check name hashes, list the records one registry adds, compare records and tags.

## Item spawning (found 2026-10-21)

- The world spawns items from the **loot tree**, not by `_itemSpawnTypes`. Spawner presets (`Items/SpawnerPresets2/...`)
  name nodes `ItemLootTreeNodes.<Place>...`. The nodes are rows of tag tables `Data/Tables/Items/Spawning/Nodes/ILTN_*`
  (listed in `SCUM/Config/DefaultGameplayTags.ini`), and a leaf's last part is the item's name
  (`ItemLootTreeNodes.Kitchen.Items.Misc.Book_Cooking_BBQ`).
  No spawner preset uses `ItemSpawnType.*` tags. Of the cookbooks, only Book_Cooking_BBQ is in the tree, so the
  other books (and clones of them) never spawn in the world.
- `addLootNode` adds a leaf for a cloned item (a copy of a sibling row, optionally with another rarity). Whether the
  game then spawns it still has to be checked in game.
- `setTags` on `_itemSpawnTypes` also updates the registry tag `SpawnTypes`, so the two stay the same. The registry
  tag `Location` (a number) is not derived from `_itemLocation` in any simple way (parent class defaults), so `setBool`
  on `_itemLocation` leaves it as it is.
- `modkit tags [prefix]` lists the game's tags (`ItemSpawnType.`, `ItemLootTreeNodes.`...) with how many registry
  records use each; `--json` for programs.
