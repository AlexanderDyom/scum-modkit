// The installed game as a read-only source: its encrypted stock paks, mounted through CUE4Parse.
// The AES key is read from a file and never printed or passed on.
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using RegistryMerger;

namespace ScumModKit.Build;

public sealed class GameSource : IDisposable
{
    public const string RegistryPath = "SCUM/AssetRegistry.bin";

    private readonly DefaultFileProvider _provider;

    /// <summary>The game's Paks folder (SCUM/Content/Paks).</summary>
    public string Paks { get; }

    private GameSource(string paks, DefaultFileProvider provider)
    {
        Paks = paks;
        _provider = provider;
    }

    /// <summary>
    /// Mounts the stock paks of <paramref name="paks"/> (the folder itself, not ~mods) with the key from
    /// <paramref name="keyFile"/> (first line, hex, optional 0x).
    /// </summary>
    public static GameSource Open(string paks, string keyFile)
    {
        paks = GameLocator.ResolvePaks(paks) ?? throw new DirectoryNotFoundException($"No SCUM Paks folder at {paks}.");
        var key = MergeEngine.NormalizeKey(File.ReadLines(keyFile).FirstOrDefault())
                  ?? throw new FormatException($"{keyFile} holds no AES key.");
        var provider = new DefaultFileProvider(paks, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
        try
        {
            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey(key));
        }
        catch
        {
            provider.Dispose();
            throw;
        }
        if (provider.UnloadedVfs.Count > 0 && provider.MountedVfs.Count == 0)
        {
            provider.Dispose();
            throw new InvalidOperationException("The AES key does not open the game's paks.");
        }
        return new GameSource(paks, provider);
    }

    private IEnumerable<IVfsReader> StockPaks => _provider.MountedVfs.Cast<IVfsReader>()
        .Where(v => !Path.GetFileNameWithoutExtension(v.Name).EndsWith("_P", StringComparison.OrdinalIgnoreCase));

    /// <summary>The game's own SCUM/AssetRegistry.bin (from the highest-priority stock pak that has one).</summary>
    public byte[] ReadRegistryBytes()
    {
        var pak = StockPaks.Where(v => v.Files.ContainsKey(RegistryPath))
            .OrderBy(v => v.Path, PakPriority.Comparer(Paks)).LastOrDefault()
            ?? throw new InvalidOperationException("No stock pak with SCUM/AssetRegistry.bin found.");
        return pak.Files[RegistryPath].Read();
    }

    public AssetRegistry ReadRegistry() => AssetRegistry.Load(ReadRegistryBytes());

    /// <summary>A .sig of a stock pak, copied next to mod paks (the game does not check it for mods).</summary>
    public string SigTemplate()
    {
        var preferred = Path.Combine(Paks, "pakchunk44-WindowsNoEditor.sig"); // what the mods' build scripts copy
        if (File.Exists(preferred)) return preferred;
        return Directory.EnumerateFiles(Paks, "*.sig").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
               ?? throw new FileNotFoundException($"No .sig in {Paks}.");
    }

    public void Dispose() => _provider.Dispose();
}
