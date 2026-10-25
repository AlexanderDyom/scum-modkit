// The installed game and the AES key of its paks, for the tests that read them. Both come from the environment only:
//   SCUM_GAME          the game folder (else the SCUM install found in the Steam libraries)
//   SCUM_AES_KEY_FILE  a text file whose first line is the key (never printed, never stored in the repository)
// Without them those tests return without checking anything.
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using ScumModKit.Build;
using ScumModKit.Game;

namespace ScumModKit.Tests;

public static class GameFixture
{
    private static readonly Lazy<GameSource?> Game = new(Open);
    private static readonly Lazy<DefaultFileProvider?> Paks = new(OpenProvider);

    /// <summary>The game folder when the key file is given and SCUM is found, else null.</summary>
    public static string? GameFolder
    {
        get
        {
            var key = Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE");
            if (string.IsNullOrEmpty(key) || !File.Exists(key)) return null;
            var folder = Environment.GetEnvironmentVariable("SCUM_GAME") ?? GameLocator.FindInstall(InstallKind.Client);
            return folder != null && GameLocator.ResolvePaks(folder) != null ? folder : null;
        }
    }

    /// <summary>The game, or null when SCUM or the key is not available (the test then checks nothing).</summary>
    public static GameSource? TryGame() => Game.Value;

    /// <summary>A CUE4Parse provider of the game's stock paks (to read packages the way other tools do), or null.</summary>
    public static DefaultFileProvider? Provider() => Paks.Value;

    private static GameSource? Open() =>
        GameFolder is { } folder ? GameSource.Open(folder, Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE")!) : null;

    private static DefaultFileProvider? OpenProvider()
    {
        if (GameFolder is not { } folder) return null;
        var key = GameSource.NormalizeKey(File.ReadLines(Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE")!).First())!;
        var p = new DefaultFileProvider(GameLocator.ResolvePaks(folder)!, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_27), StringComparer.OrdinalIgnoreCase);
        p.Initialize();
        p.SubmitKey(new FGuid(), new FAesKey(key));
        return p;
    }
}
