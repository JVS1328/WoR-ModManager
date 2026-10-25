using WoRModManager.Core;

namespace WoRModManager.Core.Tests;

public class PakNamesTests
{
    [Theory]
    [InlineData("z_maw_UI.pak", true)] [InlineData("zBlackPowder.pak", true)]
    [InlineData("Objects.pak", false)] [InlineData("../z_x.pak", false)]
    [InlineData("sub\\z_x.pak", false)] [InlineData("z_x.exe", false)]
    public void IsModPak(string n, bool ok) => Assert.Equal(ok, PakNames.IsModPak(n));

    [Fact] public void Prefix_Maw() => Assert.Equal("z_maw_", PakNames.Prefix("z_maw_Objects_Rifles.pak"));
    [Fact] public void Prefix_None() => Assert.Null(PakNames.Prefix("zBlackPowder.pak"));
}
