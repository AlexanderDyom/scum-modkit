// The installed game and the AES key of its paks, for the tests that read them. Both come from the environment only:
//   SCUM_GAME          the game folder (else the SCUM install found in the Steam libraries)
//   SCUM_AES_KEY_FILE  a text file whose first line is the key (never printed, never stored in the repository)
// Without them those tests return without checking anything.
using ScumModKit.Build;
using ScumModKit.Game;

namespace ScumModKit.Tests;

public static class GameFixture
{
    private static readonly Lazy<GameSource?> Game = new(Open);

    /// <summary>The game, or null when SCUM or the key is not available (the test then checks nothing).</summary>
    public static GameSource? TryGame() => Game.Value;

    private static GameSource? Open()
    {
        var key = Environment.GetEnvironmentVariable("SCUM_AES_KEY_FILE");
        if (string.IsNullOrEmpty(key) || !File.Exists(key)) return null;
        var folder = Environment.GetEnvironmentVariable("SCUM_GAME") ?? GameLocator.FindInstall(InstallKind.Client);
        if (folder == null || GameLocator.ResolvePaks(folder) == null) return null;
        return GameSource.Open(folder, key);
    }
}
