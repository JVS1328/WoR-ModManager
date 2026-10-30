using System.IO.Compression;
using System.Text;

namespace WoRModManager.Core.Tests.TestUtil;

// Builds tiny Generated/uniforms.bin and apparel.bin files in the game's layout.
public static class ApparelBins
{
    public static byte[] Uniforms(params (uint Key, string Name, uint Hier, uint[] Ids)[] us) => Build(w =>
    {
        w.Write((uint)us.Length);
        foreach (var u in us)
        {
            w.Write(u.Key); CString(w, u.Name); w.Write(u.Hier); w.Write((byte)0);
            w.Write((ushort)u.Ids.Length);
            foreach (var id in u.Ids) w.Write(id);
        }
    });

    public static byte[] Apparel(params (uint Id, string Name, byte Cat, uint[] Children)[] items) => Build(w =>
    {
        w.Write((uint)items.Length);
        foreach (var a in items)
        {
            w.Write(a.Id); CString(w, a.Name); w.Write(a.Cat); w.Write(0u);
            w.Write((ushort)a.Children.Length);
            foreach (var c in a.Children) w.Write(c);
            w.Write(new byte[] { 0xFF, 0xFE, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }); // undecoded tail
        }
    });

    public static byte[] Pak(byte[] uniforms, byte[] apparel)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = z.CreateEntry("Generated/uniforms.bin").Open()) s.Write(uniforms);
            using (var s = z.CreateEntry("Generated/apparel.bin").Open()) s.Write(apparel);
        }
        return ms.ToArray();
    }

    static void CString(BinaryWriter w, string s) { w.Write(Encoding.ASCII.GetBytes(s)); w.Write((byte)0); }

    static byte[] Build(Action<BinaryWriter> write)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) write(w);
        return ms.ToArray();
    }
}
