using System.IO.Compression;
using WoRModManager.Core.Install;
using WoRModManager.Core.State;

namespace WoRModManager.Core.Import;

public enum ImportKind { NewMod, UpdateOf, AdoptOfficial, ReShadePreset, Nothing }

public record ImportAnalysis(ImportKind Kind, IReadOnlyList<string> PakNames, string? TargetModId, OfficialMod? Official,
    string SuggestedName, IReadOnlyList<string> Conflicts);

public static class ZipImporter
{
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    const long MaxIniBytes = 1 << 20;

    public static ImportAnalysis Analyze(string path, IReadOnlyList<ModRecord> mods, IReadOnlyList<OfficialMod> official)
    {
        List<string> names;
        var preset = false;
        if (IsBarePak(path)) names = [.. new[] { Path.GetFileName(path) }.Where(PakNames.IsModPak)];
        else
        {
            using var z = ZipFile.OpenRead(path);
            names = Paks(z).Select(e => Path.GetFileName(e.FullName)).Distinct(Ci).ToList();
            preset = names.Count == 0 && z.Entries.Where(IsSafe).Any(IsPresetIni);
        }

        ImportAnalysis Result(ImportKind k, string? target = null, OfficialMod? o = null) =>
            new(k, names, target, o, Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' '),
                names.Where(n => mods.Any(m => !Ci.Equals(m.Id, target) && m.Paks.Contains(n, Ci))).ToList());

        if (names.Count == 0) return Result(preset ? ImportKind.ReShadePreset : ImportKind.Nothing);
        if (ModService.MatchOfficial(names, official) is { } off) return Result(ImportKind.AdoptOfficial, off.Repo.Id, off);

        var top = names.Select(n => StateStore.OwnerOf(mods, n)).OfType<ModRecord>().GroupBy(m => m).MaxBy(g => g.Count());
        if (top is not null && top.Count() * 2 > names.Count)
        {
            var prefixes = top.Key.Paks.Select(PakNames.Prefix).OfType<string>().ToHashSet(Ci);
            if (names.Where(n => StateStore.OwnerOf(mods, n) is null).All(n => PakNames.Prefix(n) is { } pre && prefixes.Contains(pre)))
                return Result(ImportKind.UpdateOf, top.Key.Id);
        }
        return Result(ImportKind.NewMod);
    }

    public static ModRecord Apply(ImportAnalysis a, string path, GamePaths p, List<ModRecord> mods, string? nameOverride,
        bool allowConflicts = false)
    {
        if (a.Kind is not (ImportKind.NewMod or ImportKind.UpdateOf or ImportKind.AdoptOfficial))
            throw new InvalidOperationException($"There is no pak mod to import ({a.Kind}).");
        if (a.Conflicts.Count > 0 && !allowConflicts)
            throw new InvalidOperationException($"Already used by another mod: {string.Join(", ", a.Conflicts)}");
        if (a.PakNames.Count == 0 || !a.PakNames.All(PakNames.IsModPak)) throw new ArgumentException("Only z*.pak files can be imported.");

        var name = nameOverride ?? a.SuggestedName;
        var existing = mods.FirstOrDefault(m => Ci.Equals(m.Id, a.TargetModId));
        var rec = a.Kind switch
        {
            ImportKind.UpdateOf => existing ?? throw new InvalidOperationException($"No installed mod {a.TargetModId}."),
            ImportKind.AdoptOfficial => existing ?? new ModRecord { Id = a.TargetModId!, Name = a.Official?.Name ?? a.TargetModId!, Enabled = true },
            _ => new ModRecord { Id = ModRecord.ImportedId(name, mods), Name = name, Source = ModSource.Imported, Enabled = true },
        };
        var loc = rec.Location(p);

        var stage = Path.Combine(p.Staging, "import");
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            Directory.CreateDirectory(stage);
            var staged = new Dictionary<string, string>(Ci);
            if (IsBarePak(path)) Stage(staged, stage, Path.GetFileName(path), a.PakNames, d => File.Copy(path, d));
            else
            {
                using var z = ZipFile.OpenRead(path);
                foreach (var e in Paks(z)) Stage(staged, stage, Path.GetFileName(e.FullName), a.PakNames, d => e.ExtractToFile(d));
            }
            if (staged.Count != a.PakNames.Count) throw new InvalidDataException("The file changed after it was checked; add it again.");

            Directory.CreateDirectory(loc);
            InstallTransaction.Commit(p, loc, staged, []);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }

        if (a.Kind == ImportKind.AdoptOfficial)
        {
            rec.Source = ModSource.Official;
            rec.RepoId = rec.Id;
            rec.Version = null; // unknown, so the next refresh compares digests and reports truthfully
        }
        if (!mods.Contains(rec)) mods.Add(rec);
        foreach (var m in mods.Where(m => m != rec && Ci.Equals(m.Location(p), loc)))
            m.Paks.RemoveAll(n => a.PakNames.Contains(n, Ci)); // the overwritten files now belong to rec
        rec.Paks = [.. rec.Paks.Union(a.PakNames, Ci)];
        new StateStore(p).Save(mods);
        return rec;
    }

    static bool IsBarePak(string path) => path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);

    static bool IsSafe(ZipArchiveEntry e) => !e.FullName.Contains("..") && !Path.IsPathRooted(e.FullName);

    static IEnumerable<ZipArchiveEntry> Paks(ZipArchive z) =>
        z.Entries.Where(e => IsSafe(e) && PakNames.IsModPak(Path.GetFileName(e.FullName)));

    // Flattened: the first entry of each analysed name wins; the name is re-checked before it becomes a path.
    static void Stage(Dictionary<string, string> staged, string stage, string n, IReadOnlyList<string> wanted, Action<string> write)
    {
        if (!PakNames.IsModPak(n) || staged.ContainsKey(n) || !wanted.Contains(n, Ci)) return;
        var dest = Path.Combine(stage, n);
        write(dest);
        staged[n] = dest;
    }

    static bool IsPresetIni(ZipArchiveEntry e)
    {
        if (!e.FullName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || e.Length > MaxIniBytes) return false;
        using var r = new StreamReader(e.Open());
        for (var line = r.ReadLine(); line is not null; line = r.ReadLine())
            if (line.TrimStart().StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
