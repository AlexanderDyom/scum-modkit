# Building ScumModKit

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows; the library itself is plain .NET 8).
- For anything that reads the game: SCUM installed, and the AES key of its paks in a text file (first line, hex,
  optional `0x`). The key is not part of this repository and must never be committed.

NuGet packages (restored by `dotnet build`): CUE4Parse 1.2.2, System.IO.Hashing 8.0.0; tests: xUnit.

## Build

```
dotnet build ScumModKit.sln -c Release
ScumModKit.Cli\bin\Release\net8.0\modkit.exe --help
```

A folder with `modkit.exe` that other scripts can call:

```
dotnet publish ScumModKit.Cli -c Release -o publish
```

## Tests

```
dotnet test ScumModKit.Tests
```

The format tests need nothing. The game tests read the installed game and need:

- `SCUM_AES_KEY_FILE`: path to the key file;
- `SCUM_GAME` (optional): the game folder; otherwise the SCUM install found in the Steam libraries.

Without the key file those tests pass without checking anything.

**Never commit the key** and don't paste it into issues, logs or screenshots. `key.txt` and `*.key` are in
`.gitignore`; better keep the key file outside the repository and point `SCUM_AES_KEY_FILE` to it. Game files
(`*.uasset`, `*.uexp`, `*.pak`, `AssetRegistry.bin`...) and build folders (`stage/`, `build/`) are ignored too.

## Using it from another program

Add this repository as a git submodule and reference the projects:

```
git submodule add https://github.com/AlexanderDyom/scum-modkit modkit
git -C modkit checkout v0.1.0
```

```xml
<ProjectReference Include="..\..\modkit\ScumModKit.Build\ScumModKit.Build.csproj" />
```

`ScumModKit.Build` brings `ScumModKit.Core` with it. Pin the submodule to a release tag.

## Layout

- `ScumModKit.Core`: file formats, no game access: pak writer/reader, asset registry, cooked packages and tagged
  properties, DataTables, texts and .locres, CityHash, clone renaming.
- `ScumModKit.Build`: the game as a source (CUE4Parse), stage, cloning, recipe and package edits, `modkit.json`,
  building and packing a mod.
- `ScumModKit.Cli`: the `modkit` command line.
- `ScumModKit.Tests`: xUnit tests.
- `migrate/`: Python helpers that turn older Python-based mod builds into `modkit.json`.
