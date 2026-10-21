// modkit build: modkit.json -> stage (clones, the journal's texts, included files, cooking edits, edits) -> pak + .sig
// + registry.
namespace ScumModKit.Build;

public sealed class BuildOptions
{
    public required string Project { get; init; }
    /// <summary>Stage folder; default: the project's "stage". It must be empty or missing (no edits applied twice).</summary>
    public string? Stage { get; init; }
    public string? OutputPak { get; init; }
    public required string Paks { get; init; }
    /// <summary>File with the AES key; or give <see cref="Key"/> itself.</summary>
    public string? KeyFile { get; init; }
    /// <summary>The AES key itself (hex), instead of <see cref="KeyFile"/>.</summary>
    public string? Key { get; init; }
    /// <summary>Only build the stage, no pak.</summary>
    public bool NoPack { get; init; }
    public string? DataOrderFrom { get; init; }
}

public static class ModBuilder
{
    public static (int Clones, PackResult? Pack) Build(BuildOptions o, Action<string>? log = null)
    {
        log ??= _ => { };
        var project = ModProject.Load(o.Project);
        var stageDir = o.Stage ?? project.Resolve(project.Stage) ?? throw new ArgumentException("no stage folder");
        if (Directory.Exists(stageDir) && Directory.EnumerateFileSystemEntries(stageDir).Any())
            throw new InvalidOperationException($"The stage {stageDir} is not empty; build into an empty folder.");
        Directory.CreateDirectory(stageDir);

        var groups = project.CloneGroups();
        var registryTags = new List<(string, string, string)>();
        using (var game = o.Key != null ? GameSource.OpenWithKey(o.Paks, o.Key)
                   : GameSource.Open(o.Paks, o.KeyFile ?? throw new ArgumentException("no AES key (KeyFile or Key)")))
        {
            var stage = new Stage(stageDir, game);
            if (groups.Count > 0)
            {
                var files = PackageCloner.CloneInto(game, groups, stageDir);
                log($"clones: {groups.Count} ({files.Count} files)");
            }
            var journalEdits = project.JournalEdits();
            if (journalEdits.Count > 0)
            {
                Ops.EditList.Apply(stage, journalEdits);
                log($"journal texts: {journalEdits.Count}");
            }
            foreach (var inc in project.Include)
            {
                var n = Include(project.Resolve(inc.From)!, Path.Combine(stageDir, inc.To));
                log($"include {inc.From} -> {inc.To}: {n} files");
            }
            if (project.Cooking != null)
            {
                project.Cooking.Apply(stage);
                log($"cooking: {project.Cooking.Recipes.Count} recipes edited");
            }
            if (project.Edits.Count > 0)
            {
                Ops.EditList.Apply(stage, project.Edits);
                log($"edits: {project.Edits.Count}");
            }
            registryTags.AddRange(stage.RegistryTags);
        }
        if (o.NoPack) return (groups.Count, null);

        var outPak = o.OutputPak ?? Path.Combine(project.Folder, "build", (project.Pak ?? throw new ArgumentException("no \"pak\" name in the project")) + ".pak");
        var r = ModPacker.Pack(new PackOptions
        {
            Stage = stageDir, OutputPak = outPak, Clones = groups, Paks = o.Paks, KeyFile = o.KeyFile, Key = o.Key, DataOrderFrom = o.DataOrderFrom,
            RegistryTags = registryTags,
        });
        return (groups.Count, r);
    }

    /// <summary>Copies a file or a folder's files into the stage.</summary>
    private static int Include(string from, string to)
    {
        if (File.Exists(from))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
            return 1;
        }
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException($"include: {from} not found.");
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var t = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(t)!);
            File.Copy(f, t, true);
            n++;
        }
        return n;
    }
}
