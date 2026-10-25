using WoRModManager.Core.ReShade;

namespace WoRModManager.Core.Tests;

public class IniFileTests
{
    const string Text = "; comment\r\n[GENERAL]\r\nEffectSearchPaths=.\\a\\**\r\nPresetPath=.\\old.ini\r\n  ; indented comment\r\n\r\n[INPUT]\r\nKey=1\r\n";

    static string Write(string text)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("wormm-ini").FullName, "ReShade.ini");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact] public void Ini_Set_ChangesOnlyThatLine()
    {
        var path = Write(Text);
        var ini = IniFile.Load(path);
        Assert.Equal(@".\old.ini", ini.Get("general", "presetpath"));
        ini.Set("GENERAL", "PresetPath", @".\new.ini");
        ini.Save(path);
        Assert.Equal(Text.Replace(@".\old.ini", @".\new.ini"), File.ReadAllText(path));
    }

    [Fact] public void Ini_Set_AddsMissingKeyToSection()
    {
        var path = Write(Text);
        var ini = IniFile.Load(path);
        Assert.Null(ini.Get("GENERAL", "TextureSearchPaths"));
        ini.Set("GENERAL", "TextureSearchPaths", @".\t");
        ini.Set("NEW", "A", "b");
        ini.Save(path);
        Assert.Equal(Text.Replace("  ; indented comment\r\n", "  ; indented comment\r\nTextureSearchPaths=.\\t\r\n") + "\r\n[NEW]\r\nA=b\r\n",
            File.ReadAllText(path));
        Assert.Equal(@".\t", IniFile.Load(path).Get("GENERAL", "TextureSearchPaths"));
    }
}
