// A mod's stage: a folder with the pak's content as mounted (SCUM/Content/..., SCUM/Config/...). Packages are named by
// their /Game path; a game package that is not in the stage yet is copied from the game on first use.
using ScumModKit.Package;

namespace ScumModKit.Build;

public sealed class Stage(string root, GameSource? game = null)
{
    public string Root { get; } = Path.GetFullPath(root);
    public GameSource? Game { get; } = game;

    /// <summary>Path of a package's .uasset in the stage (/Game/X -> Root/SCUM/Content/X.uasset).</summary>
    public string UassetPath(string package) => Path.Combine(Root, GameSource.PakPath(package) + ".uasset");

    public bool Has(string package) => File.Exists(UassetPath(package));

    /// <summary>Copies the game's package into the stage unless the stage has it.</summary>
    public void Ensure(string package)
    {
        if (Has(package)) return;
        if (Game == null) throw new FileNotFoundException($"{package} is not in the stage (and no game to copy it from).");
        Game.ExtractTo(package, Root);
    }

    /// <summary>Loads the package (copied from the game first if needed), runs the edit, saves it in place.</summary>
    public void Edit(string package, Action<CookedPackage> edit)
    {
        Ensure(package);
        var path = UassetPath(package);
        var pkg = CookedPackage.Load(path);
        edit(pkg);
        pkg.Save(path);
    }
}
