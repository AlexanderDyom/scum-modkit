# Third-party notices

ScumModKit links against the following open-source packages (versions in the `.csproj` files). Each keeps its own
licence; no source code from these projects is copied into this repository.

| Package | Purpose | Licence |
|---|---|---|
| [CUE4Parse](https://github.com/FabianFG/CUE4Parse) 1.2.2 | reading the game's encrypted pak files (packages, asset registry, config) | Apache-2.0 |
| [System.IO.Hashing](https://github.com/dotnet/runtime) 8.0.0 | CRC-32 for localization (.locres) hashes | MIT |
| [.NET 8](https://github.com/dotnet) | runtime | MIT |

Transitive dependencies of CUE4Parse (Newtonsoft.Json, K4os LZ4 and xxHash, ZstdSharp, Oodle.NET, Zlib-ng.NET,
BouncyCastle, Serilog, Blake3, LZMA-SDK, Infrablack.UE4Config, OffiUtils) keep their respective licences
(MIT / Apache-2.0 / BSD).

The tests use [xUnit](https://github.com/xunit/xunit) (Apache-2.0), Microsoft.NET.Test.Sdk (MIT) and
coverlet.collector (MIT); they are not part of the library.

## References (no code taken)

- [repak](https://github.com/trumank/repak) (MIT / Apache-2.0). The pak v11 layout ScumModKit writes was checked
  byte for byte against repak's output; no repak code is included.
- Unreal Engine 4.27 file formats (pak, cooked packages, asset registry, locres) as documented by its source and by
  CUE4Parse. CityHash64 is reimplemented after Google's public CityHash v1.1 algorithm description.
- [ScumRegistryMerger](https://github.com/AlexanderDyom/scum-registry-merger) by the same author, from which the
  asset registry and pak writer code was moved here (MIT).

## Game files

SCUM and its assets are the property of Gamepires / Jagex. ScumModKit reads the user's own installed game files and
never redistributes them. This repository contains no game files (no packages, paks, registries or extracted data)
and no AES key of the game's paks: the key is read at run time from a file the user points to and is never printed or
stored. Mods built with it are made on the user's own computer from that user's installed game. This project is not
affiliated with Gamepires or Jagex.
