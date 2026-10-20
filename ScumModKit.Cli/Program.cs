// modkit: command line of ScumModKit.
using ScumModKit.Game;
using ScumModKit.Pak;
using ScumModKit.Registry;
using ScumModKit.Build;
using ScumModKit.Hashing;

try
{
    return Cli.Run(args);
}
catch (Exception ex) when (ex is FormatException or IOException or InvalidOperationException or UnauthorizedAccessException
                               or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

static class Cli
{
    const string Usage = """
        modkit - SCUM mod compiler (ScumModKit).

        Usage:
          modkit pack [<modkit.json>] [--stage <dir>] [--out <file.pak> | --out-dir <dir>]
                      [--journal <journal.jsonl>] [--clone </Game/Old>=</Game/New>[,</Game/Old2>=</Game/New2>...]]...
                      [--registry auto|build|keep|none] [--registry-out <AssetRegistry.bin>]
                      [--paks <SCUM folder or Paks>] [--key <key file>] [--sig <file>|none] [--data-order-from <pak>]
              Packs the stage folder into a pak v11 (byte-identical to `repak pack --version V11 -m ../../../`), copies a
              stock .sig next to it and, when there are clones, ships our SCUM/AssetRegistry.bin: the game's full registry
              plus records for the cloned packages (copies of the templates' records, renamed).
              Clones come from modkit.json ("clones", "journal"), --journal (ScumStudio cloneAsset edits) and --clone
              (one group per option: the first pair is the primary asset).
              --registry auto: build when there are clones, else ship the stage's registry file as it is.
              --key: default env SCUM_AES_KEY_FILE, else key.txt in the current folder or the stage's, or their parents.
              --paks: default the SCUM install found in the Steam libraries.
              --data-order-from: write the file data in that pak's order. repak packs in parallel, so its data order
              varies between runs; with its order our pak is byte-identical to repak's (the index is always the same).

          modkit reg game <paks> <key file> <out.bin>   the game's own AssetRegistry.bin
          modkit reg hashes <AssetRegistry.bin>         check every name hash of the name batch (CityHash64)
          modkit reg diff <base.bin> <other.bin>        records of other that base lacks, with tags
          modkit reg compare <a.bin> <b.bin>            same records, fields and tags (order-sensitive)?
          modkit reg show <AssetRegistry.bin> <text>    records whose object path contains text
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        return args[0] switch
        {
            "pack" => Pack(args[1..]),
            "reg" when args.Length >= 2 => Reg(args[1], args[2..]),
            _ => throw new ArgumentException($"unknown command {args[0]} (modkit --help)"),
        };
    }

    // ------------------------------------------------------------------ pack

    static int Pack(string[] args)
    {
        string? project = null, stage = null, orderFrom = null, outPak = null, outDir = null, journal = null, paks = null, key = null, sig = null, regOut = null;
        var mode = RegistryMode.Auto;
        var clones = new List<CloneGroup>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--stage": stage = Next(); break;
                case "--out": outPak = Next(); break;
                case "--out-dir": outDir = Next(); break;
                case "--journal": journal = Next(); break;
                case "--paks": paks = Next(); break;
                case "--key": key = Next(); break;
                case "--sig": sig = Next(); break;
                case "--registry-out": regOut = Next(); break;
                case "--data-order-from": orderFrom = Next(); break;
                case "--registry": mode = Enum.Parse<RegistryMode>(Next(), ignoreCase: true); break;
                case "--clone":
                {
                    var pairs = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.Split('=', 2)).Select(p => p.Length == 2 ? new PackagePair { Old = p[0], New = p[1] } : throw new ArgumentException("--clone wants /Game/Old=/Game/New"))
                        .ToList();
                    clones.Add(new CloneSpec { Packages = pairs }.ToGroup());
                    break;
                }
                default:
                    if (args[i].StartsWith('-') || project != null) throw new ArgumentException($"unexpected argument {args[i]}");
                    project = args[i];
                    break;
            }
        }

        var groups = new Dictionary<string, CloneGroup>(StringComparer.OrdinalIgnoreCase);
        string? pakName = null;
        if (project != null)
        {
            var p = ModProject.Load(project);
            stage ??= p.Resolve(p.Stage);
            pakName = p.Pak;
            foreach (var g in p.CloneGroups()) groups[g.NewPrimary] = g;
        }
        if (journal != null) foreach (var g in ScumStudioJournal.ReadClones(journal)) groups[g.NewPrimary] = g;
        foreach (var g in clones) groups[g.NewPrimary] = g;

        if (stage == null) throw new ArgumentException("no stage folder (--stage or \"stage\" in modkit.json)");
        if (outPak == null)
        {
            if (pakName == null) throw new ArgumentException("no output (--out, or --out-dir with \"pak\" in modkit.json)");
            outPak = Path.Combine(outDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project!))!, "build"), pakName + ".pak");
        }

        paks ??= GameLocator.FindInstall(InstallKind.Client);
        key ??= Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE") is { Length: > 0 } env ? env : FindKey(Environment.CurrentDirectory) ?? FindKey(stage);

        var r = ModPacker.Pack(new PackOptions
        {
            Stage = stage, OutputPak = outPak, Clones = [.. groups.Values], Registry = mode, Paks = paks, KeyFile = key,
            Sig = sig == "none" ? "" : sig, RegistryOut = regOut, DataOrderFrom = orderFrom,
        });

        Console.WriteLine($"pak: {Path.GetFullPath(outPak)} ({r.Files} files, {r.PakSize:N0} bytes)");
        if (r.SigFrom != null) Console.WriteLine($"sig: copied from {Path.GetFileName(r.SigFrom)}");
        if (r.RegistryBuilt)
            Console.WriteLine($"registry: game {r.GameRecords} records + {r.Clones!.Records} clone records ({groups.Count} clones)" +
                              (r.Clones.Skipped.Count > 0 ? $", {r.Clones.Skipped.Count} packages already registered" : ""));
        else Console.WriteLine(r.RegistryShipped ? "registry: the stage's file, unchanged" : "registry: none");
        foreach (var w in r.Warnings) Console.WriteLine("warning: " + w);
        return 0;
    }

    /// <summary>key.txt in the folder or one of its parents.</summary>
    static string? FindKey(string folder)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(folder)); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "key.txt");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    // ------------------------------------------------------------------ reg

    static int Reg(string cmd, string[] a)
    {
        switch (cmd)
        {
            case "game" when a.Length == 3:
            {
                using var game = GameSource.Open(a[0], a[1]);
                File.WriteAllBytes(a[2], game.ReadRegistryBytes());
                return 0;
            }
            case "hashes" when a.Length == 1:
            {
                var reg = AssetRegistry.Load(File.ReadAllBytes(a[0]));
                var bad = 0;
                for (var i = 0; i < reg.Names.Count; i++)
                    if (CityHash.NameHash(reg.Names[i], reg.NameWide[i]) != reg.NameHashes[i] && bad++ < 10)
                        Console.WriteLine($"mismatch {i}: {reg.Names[i]} (wide={reg.NameWide[i]})");
                Console.WriteLine($"{reg.Names.Count} names, {bad} hash mismatches");
                return bad == 0 ? 0 : 2;
            }
            case "diff" when a.Length == 2:
            {
                var b = AssetRegistry.Load(File.ReadAllBytes(a[0]));
                var o = AssetRegistry.Load(File.ReadAllBytes(a[1]));
                Console.WriteLine($"names {b.Names.Count} -> {o.Names.Count}; records {b.Assets.Count} -> {o.Assets.Count}");
                var known = b.Assets.Select(x => b.NameText(x.ObjectPath)).ToHashSet();
                foreach (var x in o.Assets.Where(x => !known.Contains(o.NameText(x.ObjectPath)))) Print(o, x);
                return 0;
            }
            case "compare" when a.Length == 2:
            {
                var x = AssetRegistry.Load(File.ReadAllBytes(a[0]));
                var y = AssetRegistry.Load(File.ReadAllBytes(a[1]));
                var diffs = 0;
                Console.WriteLine($"names {x.Names.Count} vs {y.Names.Count}; store ansi {x.AnsiOffsets.Count}/{y.AnsiOffsets.Count} wide {x.WideOffsets.Count}/{y.WideOffsets.Count} " +
                                  $"nlname {x.NlNames.Count}/{y.NlNames.Count} name {x.NameValues.Count}/{y.NameValues.Count} nlexport {x.NlExports.Count}/{y.NlExports.Count} " +
                                  $"export {x.Exports.Count}/{y.Exports.Count} text {x.Texts.Count}/{y.Texts.Count} nlpairs {x.NlPairs.Count}/{y.NlPairs.Count} pairs {x.Pairs.Count}/{y.Pairs.Count}");
                var firstName = Enumerable.Range(0, Math.Min(x.Names.Count, y.Names.Count)).FirstOrDefault(i => x.Names[i] != y.Names[i] || x.NameWide[i] != y.NameWide[i], -1);
                if (firstName >= 0) Console.WriteLine($"name batch differs from #{firstName}: {x.Names[firstName]} vs {y.Names[firstName]}");
                if (x.Assets.Count != y.Assets.Count) { Console.WriteLine($"records: {x.Assets.Count} vs {y.Assets.Count}"); diffs++; }
                for (var i = 0; i < Math.Min(x.Assets.Count, y.Assets.Count); i++)
                {
                    var tx = Describe(x, x.Assets[i]);
                    var ty = Describe(y, y.Assets[i]);
                    if (tx == ty) continue;
                    if (diffs++ < 5) Console.WriteLine($"record {i} differs:\n--- {a[0]}\n{tx}\n--- {a[1]}\n{ty}");
                }
                Console.WriteLine(diffs == 0 ? $"same {x.Assets.Count} records with the same fields and tags" : $"{diffs} differences");
                return diffs == 0 ? 0 : 2;
            }
            case "names" when a.Length == 2:
            {
                var o = AssetRegistry.Load(File.ReadAllBytes(a[0]));
                for (var i = int.Parse(a[1]); i < o.Names.Count; i++) Console.WriteLine($"{i} {o.Names[i]}");
                return 0;
            }
            case "show" when a.Length == 2:
            {
                var o = AssetRegistry.Load(File.ReadAllBytes(a[0]));
                foreach (var x in o.Assets.Where(x => o.NameText(x.ObjectPath).Contains(a[1], StringComparison.OrdinalIgnoreCase))) Print(o, x);
                return 0;
            }
        }
        throw new ArgumentException($"bad reg command (modkit --help)");
    }

    static void Print(AssetRegistry reg, AssetRecord x) => Console.WriteLine(Describe(reg, x));

    /// <summary>A record as text: names, tag handle kind and tags, bundles, chunk ids, flags (no store indices).</summary>
    static string Describe(AssetRegistry reg, AssetRecord x)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{reg.NameText(x.ObjectPath)} | {reg.NameText(x.PackagePath)} | {reg.NameText(x.AssetClass)} | {reg.NameText(x.PackageName)} | {reg.NameText(x.AssetName)}");
        sb.AppendLine($"    numberless={(x.TagHandle >> 63) != 0} chunks=[{string.Join(",", x.ChunkIds)}] flags={x.PackageFlags:X}");
        foreach (var b in x.Bundles) sb.AppendLine($"    bundle {reg.NameText(b.Name)}: {string.Join(", ", b.Assets.Select(p => reg.NameText(p.Path)))}");
        foreach (var (k, v) in reg.Tags(x.TagHandle)) sb.AppendLine($"    {k} = {v}");
        return sb.ToString().TrimEnd();
    }
}
