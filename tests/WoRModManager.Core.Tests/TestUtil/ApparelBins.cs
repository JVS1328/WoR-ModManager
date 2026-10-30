using System.IO.Compression;
using System.Text;
using WoRModManager.Core.Uniforms;

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

    // Regiment records as outfitter.bin has them: crc32(id), id, Identifier, Name, then company and class data in which
    // each class's uniform id appears. The filler stands in for the parts that aren't read.
    public static byte[] Outfitter(params (string Id, string Identifier, string Name, uint[] Uniforms)[] regiments) => Build(w =>
    {
        w.Write((uint)regiments.Length);
        foreach (var r in regiments)
        {
            w.Write(Crc32.Lowercase(r.Id)); CString(w, r.Id); CString(w, r.Identifier); CString(w, r.Name);
            w.Write(new byte[] { 1, 2, 2, 0, 8, 0 }); CString(w, "A"); CString(w, "Company A"); CString(w, "colors_x_nat");
            foreach (var u in r.Uniforms) { CString(w, "Objects\\Characters\\CDF\\x.cdf"); w.Write((byte)1); w.Write(u); }
        }
    });

    public static byte[] Pak(byte[] uniforms, byte[] apparel, byte[]? outfitter = null)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = z.CreateEntry("Generated/uniforms.bin").Open()) s.Write(uniforms);
            using (var s = z.CreateEntry("Generated/apparel.bin").Open()) s.Write(apparel);
            if (outfitter is not null) using (var s = z.CreateEntry("Generated/outfitter.bin").Open()) s.Write(outfitter);
        }
        return ms.ToArray();
    }

    static void CString(BinaryWriter w, string s) { w.Write(Encoding.UTF8.GetBytes(s)); w.Write((byte)0); }

    static byte[] Build(Action<BinaryWriter> write)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) write(w);
        return ms.ToArray();
    }
}
