// Tests that need no game files.
using ScumModKit.Clone;
using ScumModKit.Hashing;
using ScumModKit.Package;
using ScumModKit.Pak;
using ScumModKit.Properties;
using ScumModKit.Text;

namespace ScumModKit.Tests;

public class FormatTests
{
    [Fact]
    public void CityHash64_of_empty_input_is_k2() => Assert.Equal(0x9ae16a3b2f90404fUL, CityHash.Hash64([]));

    [Fact]
    public void CityHash64_covers_every_length_branch()
    {
        // each length range has its own code path; the hash must be stable and differ between inputs
        var seen = new HashSet<ulong>();
        foreach (var n in new[] { 1, 3, 4, 8, 16, 17, 32, 33, 64, 65, 200 })
            Assert.True(seen.Add(CityHash.Hash64(Enumerable.Range(0, n).Select(i => (byte)(i * 7)).ToArray())));
    }

    [Fact]
    public void Name_hash_ignores_ascii_case() =>
        Assert.Equal(CityHash.NameHash("/Game/ConZ_Files/Items", false), CityHash.NameHash("/game/conz_files/ITEMS", false));

    [Fact]
    public void Pak_writer_output_reads_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"modkit-test-{Guid.NewGuid():N}.pak");
        try
        {
            var files = new List<(string, byte[])>
            {
                ("SCUM/Content/B/two.uasset", [1, 2, 3]),
                ("SCUM/Content/A/one.uexp", [.. Enumerable.Repeat((byte)9, 100)]),
                ("SCUM/Config/Tags/x.ini", [.. "[x]"u8]),
            };
            PakWriter.Write(path, files);
            var index = PakIndex.Read(path);
            Assert.Equal("../../../", index.MountPoint);
            var bytes = File.ReadAllBytes(path);
            foreach (var (name, data) in files)
            {
                var e = index.Entries.Single(x => x.Path == name);
                Assert.Equal(data.Length, e.Size);
                Assert.Equal(data, bytes.AsSpan((int)e.Offset + 53, (int)e.Size).ToArray()); // 53 = entry header
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Clone_renames_whole_paths_and_words_only()
    {
        var rename = CloneRenamer.For([new PackageRename("/Game/F/Dish_Salad_Tomato", "/Game/F/Dish_Mod_Salad")]);
        Assert.Equal("/Game/F/Dish_Mod_Salad", rename("/Game/F/Dish_Salad_Tomato"));
        Assert.Equal("/Game/F/Dish_Mod_Salad.Dish_Mod_Salad_C", rename("/Game/F/Dish_Salad_Tomato.Dish_Salad_Tomato_C"));
        Assert.Equal("Default__Dish_Mod_Salad_C", rename("Default__Dish_Salad_Tomato_C"));
        Assert.Equal("BP_Dish_Salad_Tomato", rename("BP_Dish_Salad_Tomato"));                   // another asset
        Assert.Equal("/Game/Other/Dish_Salad_Tomato", rename("/Game/Other/Dish_Salad_Tomato")); // another package
        Assert.Equal("/Game/F/Dish_Salad_Tomato_ES", rename("/Game/F/Dish_Salad_Tomato_ES"));   // not in the clone
    }

    [Fact]
    public void Text_values_read_back()
    {
        Assert.Equal("Безумный салат", TextValues.Source(TextValues.Invariant("Безумный салат")));
        Assert.Equal("Old Chest", TextValues.Source(TextValues.Base("Mod", "Key.Caption", "Old Chest")));
    }

    [Fact]
    public void Locres_v3_has_magic_and_version() =>
        Assert.Equal(3, Locres.WriteV3("Mod", [new LocresEntry("K", "Source", "Перевод")])[16]);

    [Fact]
    public void Name_map_hashes_of_none() =>
        Assert.Equal(CookedPackage.NewEntry("None").CaseHash, NameHashes.StrCrc32("None"));

    [Fact]
    public void Tag_container_as_registry_text()
    {
        Assert.Equal("(GameplayTags=)", PropertyPath.RegistryText([]));
        Assert.Equal("(GameplayTags=((TagName=\"A.B\"),(TagName=\"C\")))", PropertyPath.RegistryText(["A.B", "C"]));
    }
}
