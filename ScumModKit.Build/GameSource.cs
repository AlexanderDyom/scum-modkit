// The installed game as a read-only source: its encrypted stock paks, mounted through CUE4Parse.
// The AES key is read from a file and never printed or passed on.
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using ScumModKit.Game;
using ScumModKit.Pak;
using ScumModKit.Registry;

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
    public static GameSource Open(string paks, string keyFile) =>
        OpenWithKey(paks, File.ReadLines(keyFile).FirstOrDefault() ?? throw new FormatException($"{keyFile} holds no AES key."));

    /// <summary>Same with the key itself (hex, optional 0x), e.g. kept encrypted by a program; it is never printed.</summary>
    public static GameSource OpenWithKey(string paks, string aesKey)
    {
        paks = GameLocator.ResolvePaks(paks) ?? throw new DirectoryNotFoundException($"No SCUM Paks folder at {paks}.");
        var key = NormalizeKey(aesKey) ?? throw new FormatException("The AES key is empty.");
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

    /// <summary>"0x" + 64 hex digits, or null for an empty key; FormatException for anything else (the key is not shown).</summary>
    public static string? NormalizeKey(string? key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) return null;
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) key = key[2..];
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new FormatException("The AES key must be 64 hex digits (optionally starting with 0x).");
        return "0x" + key.ToUpperInvariant();
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

    /// <summary>Path of a game package or folder in the paks: /Game/X -> SCUM/Content/X; SCUM/... stays as it is.</summary>
    public static string PakPath(string path)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return "SCUM/Content/" + path[6..];
        if (path.Equals("/Game", StringComparison.OrdinalIgnoreCase)) return "SCUM/Content";
        return path.TrimStart('/');
    }

    /// <summary>
    /// Files of the game (as the game sees them: the highest-priority stock pak wins) for a package
    /// (/Game/Folder/Name -> its .uasset, .uexp, .ubulk...) or every file under a folder (/Game/Folder or SCUM/...).
    /// </summary>
    public List<(string Path, byte[] Data)> Extract(string packageOrFolder)
    {
        var p = PakPath(packageOrFolder);
        var stem = Path.GetExtension(p).Length > 0 ? p[..^Path.GetExtension(p).Length] : p;
        var matches = _provider.Files.Values
            .Where(f => f.Path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase) || f.Path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                        (f.Path.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) && f.Path.IndexOf('/', stem.Length) < 0))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0) throw new FileNotFoundException($"Nothing in the game's paks at {packageOrFolder}.");
        return matches.Select(f => (f.Path, f.Read())).ToList();
    }

    /// <summary>Extracts into a stage folder (paths as in the pak); returns the files written.</summary>
    public List<string> ExtractTo(string packageOrFolder, string stage)
    {
        var written = new List<string>();
        foreach (var (path, data) in Extract(packageOrFolder))
        {
            var target = Path.Combine(stage, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
            written.Add(path);
        }
        return written;
    }

    public void Dispose() => _provider.Dispose();
}
