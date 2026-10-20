// modkit pack: a stage folder -> mod pak (v11, as repak writes it) + .sig + our registry for the cloned packages.
using ScumModKit.Game;
using ScumModKit.Pak;
using ScumModKit.Registry;

namespace ScumModKit.Build;

public enum RegistryMode
{
    /// <summary>Build our registry when there are clones, else ship the stage's registry file (if any) as it is.</summary>
    Auto,
    /// <summary>Always build: the game's full registry plus the clone records.</summary>
    Build,
    /// <summary>Ship the stage's SCUM/AssetRegistry.bin unchanged.</summary>
    Keep,
    /// <summary>Ship no registry.</summary>
    None,
}

public sealed class PackOptions
{
    /// <summary>Folder whose content is the pak's content (SCUM/Content/..., SCUM/Config/...).</summary>
    public required string Stage { get; init; }
    public required string OutputPak { get; init; }
    public IReadOnlyList<CloneGroup> Clones { get; init; } = [];
    public RegistryMode Registry { get; init; } = RegistryMode.Auto;
    /// <summary>Game Paks folder (or install folder); needed to build the registry and for the default .sig.</summary>
    public string? Paks { get; init; }
    /// <summary>File with the AES key of the game's paks (needed to build the registry).</summary>
    public string? KeyFile { get; init; }
    /// <summary>.sig copied next to the pak; null = a stock .sig of the game, "" = none.</summary>
    public string? Sig { get; init; }
    /// <summary>Also write the built registry here.</summary>
    public string? RegistryOut { get; init; }
    public string MountPoint { get; init; } = "../../../";
    /// <summary>
    /// Write the file data in the order of this pak (files it lacks go last). repak's data order varies from run to run
    /// (it packs in parallel), so this is how a build is compared byte for byte with a repak pak of the same files.
    /// </summary>
    public string? DataOrderFrom { get; init; }
}

public sealed class PackResult
{
    public int Files { get; set; }
    public long PakSize { get; set; }
    public string? SigFrom { get; set; }
    public bool RegistryBuilt { get; set; }
    public bool RegistryShipped { get; set; }
    public int GameRecords { get; set; }
    public CloneReport? Clones { get; set; }
    public List<string> Warnings { get; } = [];
}

public static class ModPacker
{
    public const string RegistryPath = GameSource.RegistryPath;

    public static PackResult Pack(PackOptions o)
    {
        var result = new PackResult();
        var stage = Path.GetFullPath(o.Stage);
        if (!Directory.Exists(stage)) throw new DirectoryNotFoundException($"No stage folder {stage}.");
        var files = StageFiles(stage);
        var stageHasRegistry = files.Any(f => f.Path.Equals(RegistryPath, StringComparison.OrdinalIgnoreCase));

        var mode = o.Registry == RegistryMode.Auto ? (o.Clones.Count > 0 ? RegistryMode.Build : RegistryMode.Keep) : o.Registry;
        if (mode != RegistryMode.Keep) files.RemoveAll(f => f.Path.Equals(RegistryPath, StringComparison.OrdinalIgnoreCase));

        GameSource? game = null;
        try
        {
            if (mode == RegistryMode.Build)
            {
                if (o.Paks == null || o.KeyFile == null) throw new ArgumentException("Building the registry needs the game's Paks folder and the AES key file.");
                game = GameSource.Open(o.Paks, o.KeyFile);
                var reg = game.ReadRegistry();
                result.GameRecords = reg.Assets.Count;
                result.Clones = RecordCloner.AddClones(reg, o.Clones, pkg => ShipsPackage(files, pkg));
                result.Warnings.AddRange(result.Clones.Warnings);
                var bytes = reg.Save();
                if (o.RegistryOut != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.RegistryOut))!);
                    File.WriteAllBytes(o.RegistryOut, bytes);
                }
                files.Add((RegistryPath, Path.Combine(stage, RegistryPath), bytes));
                result.RegistryBuilt = true;
            }
            result.RegistryShipped = files.Any(f => f.Path.Equals(RegistryPath, StringComparison.OrdinalIgnoreCase));
            if (mode == RegistryMode.Keep && !stageHasRegistry && o.Registry == RegistryMode.Keep)
                result.Warnings.Add("the stage has no SCUM/AssetRegistry.bin");

            files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            if (o.DataOrderFrom != null)
            {
                var order = PakIndex.Read(o.DataOrderFrom).Entries.ToDictionary(e => e.Path, e => e.Offset, StringComparer.Ordinal);
                files = [.. files.OrderBy(f => order.TryGetValue(f.Path, out var at) ? at : long.MaxValue)]; // stable: the rest stay sorted
            }
            var output = Path.GetFullPath(o.OutputPak);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            PakWriter.Write(output, files.Select(f => (f.Path, f.Data ?? File.ReadAllBytes(f.Source))).ToList(), o.MountPoint);
            result.Files = files.Count;
            result.PakSize = new FileInfo(output).Length;

            var sig = o.Sig;
            if (sig == null)
            {
                if (game != null) sig = game.SigTemplate();
                else if (o.Paks != null && GameLocator.ResolvePaks(o.Paks) is { } paks)
                    sig = File.Exists(Path.Combine(paks, "pakchunk44-WindowsNoEditor.sig"))
                        ? Path.Combine(paks, "pakchunk44-WindowsNoEditor.sig")
                        : Directory.EnumerateFiles(paks, "*.sig").FirstOrDefault();
                if (sig == null) result.Warnings.Add("no .sig written (game Paks folder unknown)");
            }
            if (!string.IsNullOrEmpty(sig))
            {
                File.Copy(sig, Path.ChangeExtension(output, ".sig"), true);
                result.SigFrom = sig;
            }
        }
        finally
        {
            game?.Dispose();
        }
        return result;
    }

    /// <summary>Every file under the stage, path relative to it with '/'; the content is read when the pak is written.</summary>
    public static List<(string Path, string Source, byte[]? Data)> StageFiles(string stage) =>
        Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(stage, f).Replace('\\', '/'), f, (byte[]?)null))
            .ToList();

    private static bool ShipsPackage(List<(string Path, string Source, byte[]? Data)> files, string package)
    {
        if (!package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return true; // other roots: not checked
        var p = "SCUM/Content/" + package[6..];
        return files.Any(f => f.Path.Equals(p + ".uasset", StringComparison.OrdinalIgnoreCase) || f.Path.Equals(p + ".umap", StringComparison.OrdinalIgnoreCase));
    }
}
