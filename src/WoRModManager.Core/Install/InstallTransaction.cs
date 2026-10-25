using System.Text.Json;

namespace WoRModManager.Core.Install;

public static class InstallTransaction
{
    // BackedUp = names that existed in TargetDir when the journal was written; only those may have an original to protect.
    private record Journal(string TargetDir, List<string> NewNames, List<string> RemoveNames, List<string> BackedUp);

    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static void Commit(GamePaths p, string targetDir, IReadOnlyDictionary<string, string> stagedByName,
        IReadOnlyCollection<string> removeNames, Action<string>? beforeMove = null)
    {
        foreach (var n in stagedByName.Keys.Concat(removeNames))
            if (!PakNames.IsSafePakName(n)) throw new ArgumentException($"Unsafe pak name: {n}");

        Recover(p); // an earlier interrupted install must be undone before its journal and backups are reused
        var names = stagedByName.Keys.Concat(removeNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var j = new Journal(targetDir, [.. stagedByName.Keys], [.. removeNames],
            names.Where(n => File.Exists(Path.Combine(targetDir, n))).ToList());
        WriteJournal(p, j);
        try
        {
            Directory.CreateDirectory(p.Backup);
            foreach (var n in j.BackedUp)
                Move(Path.Combine(targetDir, n), Path.Combine(p.Backup, n), toBackup: true);
            foreach (var (n, staged) in stagedByName)
            {
                beforeMove?.Invoke(n);
                Move(staged, Path.Combine(targetDir, n), toBackup: false);
            }
        }
        catch
        {
            Rollback(p, j);
            throw;
        }
        File.Delete(p.Journal);
        Directory.Delete(p.Backup, recursive: true);
    }

    public static bool Recover(GamePaths p)
    {
        if (!File.Exists(p.Journal))
        {
            // No journal means any backup is a leftover from a commit that already succeeded.
            if (Directory.Exists(p.Backup)) Directory.Delete(p.Backup, recursive: true);
            return false;
        }
        Rollback(p, JsonSerializer.Deserialize<Journal>(File.ReadAllText(p.Journal), Opts)!);
        return true;
    }

    // Idempotent, so a kill during rollback is fixed by the next Recover.
    private static void Rollback(GamePaths p, Journal j)
    {
        foreach (var n in j.NewNames.Concat(j.RemoveNames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.Combine(j.TargetDir, n);
            var backup = Path.Combine(p.Backup, n);
            if (File.Exists(backup))
            {
                File.Delete(target);
                Move(backup, target, toBackup: false);
            }
            else if (!j.BackedUp.Contains(n, StringComparer.OrdinalIgnoreCase))
                File.Delete(target); // new or half-copied new file; there was no original
            // else: the original was never moved out of targetDir (a *.tmp partial backup is discarded below)
        }
        File.Delete(p.Journal);
        if (Directory.Exists(p.Backup)) Directory.Delete(p.Backup, recursive: true);
    }

    private static void WriteJournal(GamePaths p, Journal j)
    {
        Directory.CreateDirectory(p.ModsFolder);
        var tmp = p.Journal + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(fs, j, Opts);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, p.Journal, overwrite: true);
    }

    // Same volume: File.Move is an atomic rename. Across volumes it is an unflushed copy+delete, and a half-copied
    // backup would look complete to Rollback, so copy with write-through (into *.tmp for backups) before deleting src.
    // shortcut: same path root is taken as same volume (as GamePaths.SameDrive does); upgrade if mounted-folder volumes matter.
    private static void Move(string src, string dst, bool toBackup)
    {
        if (string.Equals(Path.GetPathRoot(Path.GetFullPath(src)), Path.GetPathRoot(Path.GetFullPath(dst)), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(src, dst);
            return;
        }
        var copy = toBackup ? dst + ".tmp" : dst;
        using (var i = File.OpenRead(src))
        using (var o = new FileStream(copy, FileMode.CreateNew, FileAccess.Write))
        {
            i.CopyTo(o);
            o.Flush(flushToDisk: true);
        }
        if (toBackup) File.Move(copy, dst);
        File.Delete(src);
    }
}
