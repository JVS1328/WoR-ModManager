using System.IO.Compression;
using WoRModManager.Core.ReShade;
using WoRModManager.Core.Tests.TestUtil;

namespace WoRModManager.Core.Tests;

public class ReShadeServiceTests : IDisposable
{
    readonly TempGame game = new();
    readonly ReShadeService svc;
    string Exe => game.Paths.ExeDir;
    string IniPath => Path.Combine(Exe, "ReShade.ini");
    public void Dispose() => game.Dispose();

    public ReShadeServiceTests() => svc = new ReShadeService(game.Paths);

    void Install(string dll = "dxgi.dll")
    {
        File.WriteAllText(Path.Combine(Exe, dll), "proxy");
        File.WriteAllText(IniPath,
            "[GENERAL]\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nPresetPath=.\\ReShadePreset.ini\r\nTextureSearchPaths=.\\reshade-shaders\\Textures\\**\r\n\r\n[INPUT]\r\nKeyOverlay=36,0,0,0\r\n");
    }

    string Zip(string name, params (string Entry, string Body)[] entries)
    {
        var path = Path.Combine(game.Root, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (e, b) in entries)
            using (var w = new StreamWriter(z.CreateEntry(e).Open())) w.Write(b);
        return path;
    }

    string CoolZip() => Zip("cool.zip",
        ("Cool.ini", "Techniques=Clarity@Clarity.fx\r\n[Clarity.fx]\r\nStrength=1\r\n"),
        ("Shaders/Clarity.fx", "// fx"),
        ("Shaders/Include/Helper.fxh", "// fxh"),
        ("Textures/lut.png", "png"));

    string Get(string key) => IniFile.Load(IniPath).Get("GENERAL", key)!;

    [Fact] public void Detect_NoIni_NotInstalled()
    {
        File.WriteAllText(Path.Combine(Exe, "dxgi.dll"), "x");
        var s = svc.Detect();
        Assert.False(s.Installed);
        Assert.Null(s.IniPath);
        Assert.Equal("dxgi.dll", s.DllName);

        Install("d3d11.dll");
        File.Delete(Path.Combine(Exe, "dxgi.dll"));
        Assert.Equal(new ReShadeStatus(true, "d3d11.dll", true, IniPath), svc.Detect());
    }

    [Fact] public void SetEnabled_RenamesDll()
    {
        Install();
        svc.SetEnabled(false);
        Assert.False(File.Exists(Path.Combine(Exe, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(Exe, "dxgi.dll.off")));
        Assert.Equal(new ReShadeStatus(true, "dxgi.dll", false, IniPath), svc.Detect());

        svc.SetEnabled(true);
        Assert.True(File.Exists(Path.Combine(Exe, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(Exe, "dxgi.dll.off")));
        Assert.True(svc.Detect().Enabled);
    }

    [Fact] public void Import_AppendsSearchPathsOnce()
    {
        Install();
        var zip = CoolZip();
        svc.Import(zip, "Cool");
        var p = svc.Import(zip, "Cool");

        Assert.Equal(@".\reshade-shaders\Shaders\**,.\wormm-reshade\Cool\Shaders\**", Get("EffectSearchPaths"));
        Assert.Equal(@".\reshade-shaders\Textures\**,.\wormm-reshade\Cool\Textures", Get("TextureSearchPaths"));
        Assert.Equal("Cool", p.Name);
        Assert.Equal(Path.Combine(game.Paths.ReShadeDir, "Cool", "Cool.ini"), p.IniPath);
        Assert.Empty(p.MissingEffects);
        Assert.True(File.Exists(Path.Combine(game.Paths.ReShadeDir, "Cool", "Shaders", "Include", "Helper.fxh")));
    }

    [Fact] public void Import_RejectsExeAndTraversal()
    {
        Install();
        svc.Import(Zip("exe.zip", ("P.ini", "Techniques=A@A.fx"), ("evil.exe", "MZ"), ("sub/run.dll", "MZ")), "P");
        var dir = Path.Combine(game.Paths.ReShadeDir, "P");
        Assert.True(File.Exists(Path.Combine(dir, "P.ini")));
        Assert.False(File.Exists(Path.Combine(dir, "evil.exe")));
        Assert.False(Directory.Exists(Path.Combine(dir, "sub")));

        var bad = Zip("bad.zip", ("Q.ini", "Techniques=A@A.fx"), ("../../escape.ini", "x"));
        Assert.Throws<InvalidDataException>(() => svc.Import(bad, "Q"));
        Assert.False(Directory.Exists(Path.Combine(game.Paths.ReShadeDir, "Q")));
        Assert.False(File.Exists(Path.Combine(Exe, "escape.ini")));

        Assert.Throws<ArgumentException>(() => svc.Import(bad, @"..\x"));
        Assert.Throws<ArgumentException>(() => svc.Import(bad, " "));
    }

    [Fact] public void ListPresets_ReportsMissingEffects()
    {
        Install();
        svc.Import(Zip("m.zip", ("M.ini", "Techniques=Clarity@Clarity.fx,Old\r\n")), "M");
        var p = Assert.Single(svc.ListPresets());
        Assert.Equal(["Clarity.fx"], p.MissingEffects);

        // found through the player's own recursive search path
        Directory.CreateDirectory(Path.Combine(Exe, "reshade-shaders", "Shaders", "Deep"));
        File.WriteAllText(Path.Combine(Exe, "reshade-shaders", "Shaders", "Deep", "Clarity.fx"), "");
        Assert.Empty(svc.ListPresets().Single().MissingEffects);
    }

    [Fact] public void SetActivePreset_WritesPresetPath()
    {
        Install();
        var p = svc.Import(CoolZip(), "Cool");
        var before = File.ReadAllText(IniPath);
        svc.SetActivePreset(p.IniPath);
        Assert.Equal(@".\wormm-reshade\Cool\Cool.ini", Get("PresetPath"));
        Assert.Equal(p.IniPath, svc.ActivePreset());
        Assert.Equal(before.Replace(@"PresetPath=.\ReShadePreset.ini", @"PresetPath=.\wormm-reshade\Cool\Cool.ini"), File.ReadAllText(IniPath));
    }

    [Fact] public void DeletePreset_RemovesFolderAndSearchPaths()
    {
        Install();
        var p = svc.Import(CoolZip(), "Cool");
        svc.Import(Zip("cool2.zip", ("C2.ini", "Techniques=X@X.fx"), ("Shaders/X.fx", "")), "Cool2");
        svc.SetActivePreset(p.IniPath);

        svc.DeletePreset("Cool");
        Assert.False(Directory.Exists(Path.Combine(game.Paths.ReShadeDir, "Cool")));
        Assert.Equal(@".\reshade-shaders\Shaders\**,.\wormm-reshade\Cool2\Shaders", Get("EffectSearchPaths"));
        Assert.Equal(@".\reshade-shaders\Textures\**", Get("TextureSearchPaths"));
        Assert.Equal("", Get("PresetPath"));
        Assert.Null(svc.ActivePreset());
        Assert.Equal("Cool2", Assert.Single(svc.ListPresets()).Name);
    }
}
