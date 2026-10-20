// modkit: command line of ScumModKit.
using ScumModKit.Game;
using ScumModKit.Pak;
using ScumModKit.Clone;
using ScumModKit.Registry;
using ScumModKit.Build;
using ScumModKit.Hashing;
using ScumModKit.Package;
using ScumModKit.Properties;

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
          modkit build <modkit.json> [--stage <empty dir>] [--out <file.pak>] [--no-pack] [--paks ...] [--key ...]
                       [--data-order-from <pak>]
              Builds the whole mod: clones from the game, "include" files, "cooking" edits, then pack (pak + .sig +
              registry). The stage must be empty (default: the project's "stage").

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

          modkit extract <path>... --stage <dir> [--paks <SCUM folder or Paks>] [--key <key file>]
              Copies game files into the stage folder, as the game sees them (from its encrypted stock paks):
              a package (/Game/Folder/Name: .uasset, .uexp, .ubulk...) or every file under a folder (/Game/Folder,
              SCUM/Content/Folder).

          modkit pkg verify <file.uasset|dir>          load + save every package, compare byte for byte, check name hashes
          modkit pkg names <file.uasset>               name map, imports and exports
          modkit pkg props <file.uasset> <export>      tagged properties as a tree (arrays of structs, structs opened)
          modkit pkg table <file.uasset> [text]        DataTable rows (names containing text)

          modkit clone [<modkit.json>] [--stage <dir>] [--journal <journal.jsonl>] [--clone ...]... [--paks ...] [--key ...]
              Clones the template packages of every clone from the game into the stage under their new names: the
              name map is renamed (paths and whole-word short names, also Default__X and X_C), the rest is copied.

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
            "build" => Build(args[1..]),
            "extract" => Extract(args[1..]),
            "clone" => Clone(args[1..]),
            "reg" when args.Length >= 2 => Reg(args[1], args[2..]),
            "pkg" when args.Length >= 2 => Pkg(args[1], args[2..]),
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
                case "--clone": clones.Add(ParseClone(Next())); break;
                default:
                    if (args[i].StartsWith('-') || project != null) throw new ArgumentException($"unexpected argument {args[i]}");
                    project = args[i];
                    break;
            }
        }

        var (groupList, projStage, pakName) = CollectClones(project, journal, clones);
        stage ??= projStage;
        if (stage == null) throw new ArgumentException("no stage folder (--stage or \"stage\" in modkit.json)");
        if (outPak == null)
        {
            if (pakName == null) throw new ArgumentException("no output (--out, or --out-dir with \"pak\" in modkit.json)");
            outPak = Path.Combine(outDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project!))!, "build"), pakName + ".pak");
        }

        paks ??= GameLocator.FindInstall(InstallKind.Client);
        key ??= KeyFile(stage);

        var r = ModPacker.Pack(new PackOptions
        {
            Stage = stage, OutputPak = outPak, Clones = groupList, Registry = mode, Paks = paks, KeyFile = key,
            Sig = sig == "none" ? "" : sig, RegistryOut = regOut, DataOrderFrom = orderFrom,
        });

        Console.WriteLine($"pak: {Path.GetFullPath(outPak)} ({r.Files} files, {r.PakSize:N0} bytes)");
        if (r.SigFrom != null) Console.WriteLine($"sig: copied from {Path.GetFileName(r.SigFrom)}");
        if (r.RegistryBuilt)
            Console.WriteLine($"registry: game {r.GameRecords} records + {r.Clones!.Records} clone records ({groupList.Count} clones)" +
                              (r.Clones.Skipped.Count > 0 ? $", {r.Clones.Skipped.Count} packages already registered" : ""));
        else Console.WriteLine(r.RegistryShipped ? "registry: the stage's file, unchanged" : "registry: none");
        foreach (var w in r.Warnings) Console.WriteLine("warning: " + w);
        return 0;
    }

    // ------------------------------------------------------------------ build

    static int Build(string[] args)
    {
        string? project = null, stage = null, outPak = null, paks = null, key = null, orderFrom = null;
        var noPack = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--stage": stage = Next(); break;
                case "--out": outPak = Next(); break;
                case "--paks": paks = Next(); break;
                case "--key": key = Next(); break;
                case "--no-pack": noPack = true; break;
                case "--data-order-from": orderFrom = Next(); break;
                default:
                    if (args[i].StartsWith('-') || project != null) throw new ArgumentException($"unexpected argument {args[i]}");
                    project = args[i];
                    break;
            }
        }
        if (project == null) throw new ArgumentException("usage: modkit build <modkit.json>");
        paks ??= GameLocator.FindInstall(InstallKind.Client) ?? throw new ArgumentException("SCUM not found, use --paks");
        key ??= KeyFile(Path.GetDirectoryName(Path.GetFullPath(project))!) ?? throw new ArgumentException("no AES key file");
        var (_, r) = ModBuilder.Build(new BuildOptions
        {
            Project = project, Stage = stage, OutputPak = outPak, Paks = paks, KeyFile = key, NoPack = noPack, DataOrderFrom = orderFrom,
        }, Console.WriteLine);
        if (r != null)
        {
            Console.WriteLine($"pak: {r.Files} files, {r.PakSize:N0} bytes" + (r.RegistryBuilt ? $"; registry + {r.Clones!.Records} clone records" : ""));
            foreach (var w in r.Warnings) Console.WriteLine("warning: " + w);
        }
        return 0;
    }

    // ------------------------------------------------------------------ clone

    static int Clone(string[] args)
    {
        string? project = null, stage = null, journal = null, paks = null, key = null;
        var clones = new List<CloneGroup>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--stage": stage = Next(); break;
                case "--journal": journal = Next(); break;
                case "--paks": paks = Next(); break;
                case "--key": key = Next(); break;
                case "--clone": clones.Add(ParseClone(Next())); break;
                default:
                    if (args[i].StartsWith('-') || project != null) throw new ArgumentException($"unexpected argument {args[i]}");
                    project = args[i];
                    break;
            }
        }
        var (groups, projStage, _) = CollectClones(project, journal, clones);
        stage ??= projStage ?? throw new ArgumentException("no stage folder (--stage or \"stage\" in modkit.json)");
        if (groups.Count == 0) throw new ArgumentException("no clones");
        using var game = OpenGame(paks, key, stage);
        var files = PackageCloner.CloneInto(game, groups, stage);
        Console.WriteLine($"{groups.Count} clones, {groups.Sum(g => g.Packages.Count)} packages, {files.Count} files -> {Path.GetFullPath(stage)}");
        return 0;
    }

    static CloneGroup ParseClone(string value)
    {
        var pairs = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => p.Length == 2 ? new PackagePair { Old = p[0], New = p[1] } : throw new ArgumentException("--clone wants /Game/Old=/Game/New"))
            .ToList();
        return new CloneSpec { Packages = pairs }.ToGroup();
    }

    /// <summary>Clones of modkit.json, then the journal's, then --clone (a later clone of the same primary wins).</summary>
    static (List<CloneGroup> Groups, string? Stage, string? PakName) CollectClones(string? project, string? journal, List<CloneGroup> clones)
    {
        var groups = new Dictionary<string, CloneGroup>(StringComparer.OrdinalIgnoreCase);
        string? stage = null, pakName = null;
        if (project != null)
        {
            var p = ModProject.Load(project);
            stage = p.Resolve(p.Stage);
            pakName = p.Pak;
            foreach (var g in p.CloneGroups()) groups[g.NewPrimary] = g;
        }
        if (journal != null) foreach (var g in ScumStudioJournal.ReadClones(journal)) groups[g.NewPrimary] = g;
        foreach (var g in clones) groups[g.NewPrimary] = g;
        return ([.. groups.Values], stage, pakName);
    }

    // ------------------------------------------------------------------ extract

    static int Extract(string[] args)
    {
        string? stage = null, paks = null, key = null;
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--stage": stage = Next(); break;
                case "--paks": paks = Next(); break;
                case "--key": key = Next(); break;
                default:
                    if (args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument {args[i]}");
                    paths.Add(args[i]);
                    break;
            }
        }
        if (stage == null || paths.Count == 0) throw new ArgumentException("usage: modkit extract <path>... --stage <dir>");
        using var game = OpenGame(paks, key, stage);
        foreach (var p in paths)
        {
            var files = game.ExtractTo(p, stage);
            Console.WriteLine($"{p}: {files.Count} files");
        }
        return 0;
    }

    static GameSource OpenGame(string? paks, string? key, string near)
    {
        paks ??= GameLocator.FindInstall(InstallKind.Client) ?? throw new ArgumentException("SCUM not found, use --paks");
        key ??= KeyFile(near) ?? throw new ArgumentException("no AES key file (--key, SCUM_AES_KEY_FILE or key.txt)");
        return GameSource.Open(paks, key);
    }

    static string? KeyFile(string near) =>
        Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE") is { Length: > 0 } env ? env : FindKey(Environment.CurrentDirectory) ?? FindKey(near);

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

    // ------------------------------------------------------------------ pkg

    static int Pkg(string cmd, string[] a)
    {
        switch (cmd)
        {
            case "verify" when a.Length == 1:
            {
                var files = Directory.Exists(a[0]) ? Directory.EnumerateFiles(a[0], "*.uasset", SearchOption.AllDirectories).ToList() : [a[0]];
                int ok = 0, bad = 0, unsupported = 0;
                foreach (var f in files)
                {
                    var uasset = File.ReadAllBytes(f);
                    var uexpPath = Path.ChangeExtension(f, ".uexp");
                    var uexp = File.Exists(uexpPath) ? File.ReadAllBytes(uexpPath) : [];
                    try
                    {
                        var pkg = CookedPackage.Load(uasset, uexp);
                        var hashBad = pkg.Names.Count(n => n.NonCaseHash != NameHashes.StrihashDeprecated(n.Text) || n.CaseHash != NameHashes.StrCrc32(n.Text));
                        var (u1, u2) = pkg.Save();
                        if (u1.AsSpan().SequenceEqual(uasset) && u2.AsSpan().SequenceEqual(uexp) && hashBad == 0) ok++;
                        else { bad++; Console.WriteLine($"DIFF {f}: uasset {(u1.AsSpan().SequenceEqual(uasset) ? "same" : "differs")}, uexp {(u2.AsSpan().SequenceEqual(uexp) ? "same" : "differs")}, name hash mismatches {hashBad}"); }
                    }
                    catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException or InvalidOperationException)
                    {
                        unsupported++;
                        Console.WriteLine($"SKIP {f}: {ex.Message}");
                        if (Environment.GetEnvironmentVariable("MODKIT_DEBUG") == "1") Console.WriteLine(ex.StackTrace);
                    }
                }
                Console.WriteLine($"{ok} identical, {bad} different, {unsupported} not supported");
                return bad == 0 ? 0 : 2;
            }
            case "props" when a.Length == 2:
            {
                var pkg = CookedPackage.Load(a[0]);
                var ep = ExportProperties.Read(pkg, a[1]);
                Dump(pkg, ep.Properties, "");
                if (ep.Tail.Length > 0) Console.WriteLine($"(+ {ep.Tail.Length} native bytes)");
                return 0;
            }
            case "table" when a.Length >= 1:
            {
                var pkg = CookedPackage.Load(a[0]);
                var t = DataTable.Read(pkg, pkg.Exports[0]);
                var (u1, u2) = (t.Object.Export.Data, 0);
                var before = (byte[])t.Object.Export.Data.Clone();
                t.Save();
                Console.WriteLine($"{t.Rows.Count} rows, re-save {(before.AsSpan().SequenceEqual(t.Object.Export.Data) ? "identical" : "DIFFERENT")}");
                foreach (var (name, row) in t.Rows.Where(r => a.Length < 2 || pkg.NameText(r.Name).Contains(a[1], StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"{pkg.NameText(name)}: " + string.Join(", ", row.Select(p => $"{p.Name}:{p.Type}={Show(pkg, p)}")));
                return 0;
            }
            case "names" when a.Length == 1:
            {
                var pkg = CookedPackage.Load(a[0]);
                for (var i = 0; i < pkg.Names.Count; i++) Console.WriteLine($"name {i}: {pkg.Names[i].Text}");
                for (var i = 0; i < pkg.Imports.Count; i++)
                {
                    var im = pkg.Imports[i];
                    Console.WriteLine($"import {-i - 1}: {pkg.NameText(im.ClassPackage)}.{pkg.NameText(im.ClassName)} {pkg.ObjectPath(-i - 1)}");
                }
                for (var i = 0; i < pkg.Exports.Count; i++)
                {
                    var e = pkg.Exports[i];
                    Console.WriteLine($"export {i + 1}: {pkg.ObjectPath(i + 1)} class={pkg.ObjectPath(e.ClassIndex)} size={e.SerialSize}");
                }
                return 0;
            }
        }
        throw new ArgumentException("bad pkg command (modkit --help)");
    }

    static void Dump(CookedPackage pkg, PropertyList list, string indent)
    {
        foreach (var p in list)
        {
            PropertyList? inner = null;
            List<PropertyList>? elems = null;
            try
            {
                if (p.Type == "StructProperty") inner = p.OpenStruct(pkg);
                else if (p.Type == "ArrayProperty") elems = p.OpenArray(pkg).Elements;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or IndexOutOfRangeException) { }
            if (inner != null) { Console.WriteLine($"{indent}{p.Name}: struct"); Dump(pkg, inner, indent + "  "); }
            else if (elems != null)
            {
                Console.WriteLine($"{indent}{p.Name}: array[{elems.Count}]");
                for (var i = 0; i < elems.Count; i++) { Console.WriteLine($"{indent}  [{i}]"); Dump(pkg, elems[i], indent + "    "); }
            }
            else Console.WriteLine($"{indent}{p.Name}: {p.Type} = {Show(pkg, p)}");
        }
    }

    static string Show(CookedPackage pkg, ScumModKit.Properties.Property p) => p.Type switch
    {
        "IntProperty" => BitConverter.ToInt32(p.Value).ToString(),
        "FloatProperty" => BitConverter.ToSingle(p.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "BoolProperty" => p.Bool.ToString(),
        "NameProperty" => pkg.NameText(p.NameValue),
        "ObjectProperty" => pkg.ObjectPath(p.ObjectIndex),
        "SoftObjectProperty" or "SoftClassProperty" => pkg.NameText(p.NameValue),
        "TextProperty" => (ScumModKit.Text.TextValues.Source(p.Value) ?? "(text)") + (Environment.GetEnvironmentVariable("MODKIT_HEX") == "1" ? " " + Convert.ToHexString(p.Value) : ""),
        _ => $"[{p.Value.Length} bytes]",
    };

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
