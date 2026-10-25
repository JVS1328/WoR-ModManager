using WoRModManager.Core;

namespace WoRModManager.Core.Tests.TestUtil;

public sealed class TempGame : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "wormm-test-" + Guid.NewGuid().ToString("N"));
    public GamePaths Paths { get; }

    public TempGame()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Assets"));
        Directory.CreateDirectory(Path.Combine(Root, "bin", "win_x64"));
        Paths = new GamePaths(Root, GamePaths.DefaultModsFolder(Root));
        Directory.CreateDirectory(Paths.ModsFolder);
    }

    public string WritePak(string name, byte[] bytes)
    {
        var p = Path.Combine(Paths.Assets, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public void Dispose() => Directory.Delete(Root, true);
}
