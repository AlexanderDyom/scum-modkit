using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ScumModKit.Game;

public enum InstallKind { Client, Server }

/// <summary>Finds SCUM / SCUM Server installs and their Paks folder.</summary>
public static class GameLocator
{
    public static string SteamFolderName(InstallKind kind) => kind == InstallKind.Server ? "SCUM Server" : "SCUM";

    /// <summary>The Paks folder of a game folder: accepts the install root, its SCUM subfolder, Content, or Paks itself.</summary>
    public static string? ResolvePaks(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        var root = Path.GetFullPath(folder);
        foreach (var c in new[]
                 {
                     root, Path.Combine(root, "Paks"), Path.Combine(root, "Content", "Paks"), Path.Combine(root, "SCUM", "Content", "Paks"),
                 })
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "pakchunk0*.pak").Any()) return c;
        }
        return null;
    }

    /// <summary>Install root of a Paks folder (…\SCUM\SCUM\Content\Paks -> …\SCUM).</summary>
    public static string InstallRoot(string paks)
    {
        var d = new DirectoryInfo(paks);
        return d.Parent?.Parent?.Parent?.FullName ?? paks;
    }

    /// <summary>Steam library folders (from the registry and libraryfolders.vdf), plus the usual default paths.</summary>
    public static IEnumerable<string> SteamLibraries()
    {
        var steamRoots = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string p1) steamRoots.Add(p1);
                if (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string p2) steamRoots.Add(p2);
            }
        }
        catch (Exception)
        {
            // no registry access: fall back to default paths
        }
        steamRoots.Add(@"C:\Program Files (x86)\Steam");
        steamRoots.Add(@"C:\Program Files\Steam");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in steamRoots)
        {
            var r = root.Replace('/', '\\');
            if (Directory.Exists(r) && seen.Add(Path.GetFullPath(r))) yield return Path.GetFullPath(r);
            var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch (IOException) { continue; }
            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(lib) && seen.Add(Path.GetFullPath(lib))) yield return Path.GetFullPath(lib);
            }
        }
    }

    /// <summary>Install root of SCUM or SCUM Server in any Steam library, or null.</summary>
    public static string? FindInstall(InstallKind kind)
    {
        foreach (var lib in SteamLibraries())
        {
            var root = Path.Combine(lib, "steamapps", "common", SteamFolderName(kind));
            if (ResolvePaks(root) != null) return ActualCase(root);
        }
        return null;
    }

    /// <summary>A Paks folder near the current folder or the program (for the console tool), else the client install.</summary>
    public static string? FindPaksNearby()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var p = ResolvePaks(d.FullName);
                if (p != null) return p;
            }
        }
        return ResolvePaks(FindInstall(InstallKind.Client));
    }

    /// <summary>The path with the casing it has on disk (libraryfolders.vdf stores it lower-cased).</summary>
    public static string ActualCase(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            var result = root.ToUpperInvariant();
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Directory.EnumerateFileSystemEntries(result, part).FirstOrDefault();
                result = match ?? Path.Combine(result, part);
            }
            return result;
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>Is SCUM or SCUM Server running (its paks are locked then)?</summary>
    public static bool IsGameRunning()
    {
        foreach (var name in new[] { "SCUM", "SCUM-Win64-Shipping", "SCUMServer", "SCUMServer-Win64-Shipping" })
        {
            var ps = Process.GetProcessesByName(name);
            var running = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            if (running) return true;
        }
        return false;
    }
}
