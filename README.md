# ScumModKit

Our SCUM mod compiler: a .NET 8 library plus the `modkit` CLI. The goal is to build mod paks without ScumStudio.
The plan is in [ARCHITECTURE.md](ARCHITECTURE.md). Done so far: slice 1, `modkit pack`.

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
