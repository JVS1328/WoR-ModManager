using WoRModManager.Core;

namespace WoRModManager.Core.Tests;

public class SteamLocatorTests
{
    [Fact] public void ParseBuildId_ReadsValue() =>
        Assert.Equal("26182310", SteamLocator.ParseBuildId("\"AppState\"\n{\n\t\"appid\"\t\t\"424030\"\n\t\"buildid\"\t\t\"26182310\"\n}"));

    [Fact] public void ParseLibraryFolders_ReadsPaths()
    {
        var vdf = "\"libraryfolders\"\n{\n\"0\"\n{\n\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n}\n\"1\"\n{\n\"path\"\t\t\"D:\\\\SteamLibrary\"\n}\n}";
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, SteamLocator.ParseLibraryFolders(vdf));
    }

    [Fact] public void ReadBuildId_ReadsManifestTwoLevelsUp()
    {
        var steamapps = Directory.CreateTempSubdirectory("wormm").FullName;
        try
        {
            File.WriteAllText(Path.Combine(steamapps, "appmanifest_424030.acf"), "\"buildid\"\t\t\"42\"");
            Assert.Equal("42", SteamLocator.ReadBuildId(Path.Combine(steamapps, "common", "War of Rights")));
            Assert.Null(SteamLocator.ReadBuildId(Path.Combine(steamapps, "x", "y", "z")));
        }
        finally { Directory.Delete(steamapps, true); }
    }
}
