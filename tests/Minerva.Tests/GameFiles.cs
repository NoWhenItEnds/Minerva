using Minerva.GameState.Live;

namespace Minerva.Tests;

/// <summary>Tests that need the installed game skip themselves when Deadlock is not found.</summary>
internal static class GameFiles
{
    private static readonly Lazy<DeadlockInstall?> Lazy = new(() =>
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("DEADLOCK_DIR");
            return env != null ? DeadlockInstall.FromPath(env) : DeadlockInstall.Locate();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    });

    public static DeadlockInstall Install
    {
        get
        {
            Assert.SkipWhen(Lazy.Value == null, "Deadlock is not installed (set DEADLOCK_DIR).");
            return Lazy.Value!;
        }
    }
}
