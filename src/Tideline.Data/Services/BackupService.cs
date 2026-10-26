using System.Formats.Tar;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Tideline.Core;
using Tideline.Data.Reports;

namespace Tideline.Data.Services;

public sealed record BackupFile(string Name, long Bytes, DateTimeOffset Modified, string Kind);

public sealed record BackupResult(string DatabaseFile, string? KeysFile, long Bytes, IReadOnlyList<string> Removed);

public sealed record VerifyResult(bool Ok, string IntegrityCheck, int Year, Spread Backup, Spread? Live, bool SameAsLive, string Message);

public sealed record RestoreResult(string DatabaseFile, string? KeysDirectory, VerifyResult Check);

/// <summary>
/// Backups (docs/11-deploy.md): a consistent copy of the database made while the app runs (<c>VACUUM INTO</c>), the keys that
/// encrypt authenticator secrets beside it, thirty days kept, and a check that a copy opens and adds up to the same spread.
/// </summary>
public static class BackupService
{
    public const int KeepDays = 30;
    private const string DbPrefix = "tideline-";
    private const string KeysPrefix = "keys-";

    public static string DatabaseFileName(DateOnly day) => $"{DbPrefix}{day:yyyyMMdd}.db";

    public static string KeysFileName(DateOnly day) => $"{KeysPrefix}{day:yyyyMMdd}.tar";

    /// <summary>Copy the database (and the keys folder, if there is one) into <paramref name="outDir"/>, then drop copies older than the retention.</summary>
    public static BackupResult Create(string connectionString, string outDir, string? keysDir, DateTimeOffset now)
    {
        Directory.CreateDirectory(outDir);
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var target = Path.GetFullPath(Path.Combine(outDir, DatabaseFileName(day)));
        if (File.Exists(target)) File.Delete(target); // VACUUM INTO refuses to overwrite; one backup a day, the latest wins

        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            conn.Execute("VACUUM INTO @target", new { target }); // the path is bound, never spliced into the SQL text
        }

        string? keysFile = null;
        if (keysDir is not null && Directory.Exists(keysDir) && Directory.EnumerateFileSystemEntries(keysDir).Any())
        {
            keysFile = Path.GetFullPath(Path.Combine(outDir, KeysFileName(day)));
            if (File.Exists(keysFile)) File.Delete(keysFile);
            TarFile.CreateFromDirectory(keysDir, keysFile, includeBaseDirectory: false);
        }

        return new BackupResult(target, keysFile, new FileInfo(target).Length, Prune(outDir, day));
    }

    /// <summary>Removes copies whose date in the file name is more than <see cref="KeepDays"/> days ago. Returns the names removed.</summary>
    public static IReadOnlyList<string> Prune(string outDir, DateOnly today)
    {
        var removed = new List<string>();
        foreach (var path in Directory.EnumerateFiles(outDir))
        {
            var name = Path.GetFileName(path);
            if (DateIn(name) is not { } day || today.DayNumber - day.DayNumber <= KeepDays) continue;
            File.Delete(path);
            removed.Add(name);
        }
        return removed;
    }

    private static DateOnly? DateIn(string name)
    {
        var prefix = name.StartsWith(DbPrefix, StringComparison.Ordinal) ? DbPrefix : name.StartsWith(KeysPrefix, StringComparison.Ordinal) ? KeysPrefix : null;
        if (prefix is null || name.Length < prefix.Length + 8) return null;
        return DateOnly.TryParseExact(name.AsSpan(prefix.Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    public static IReadOnlyList<BackupFile> List(string outDir) =>
        !Directory.Exists(outDir)
            ? []
            : Directory.EnumerateFiles(outDir)
                .Where(p => DateIn(Path.GetFileName(p)) is not null)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Select(f => new BackupFile(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), f.Name.StartsWith(DbPrefix, StringComparison.Ordinal) ? "database" : "keys"))
                .ToList();

    private static Spread YearSpread(string connectionString, int year)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        TypeHandlers.EnsureRegistered();
        return new ReportRepository(conn).SpreadFor(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
    }

    /// <summary>
    /// Opens the copy read only, runs SQLite's own integrity check, and adds up the year's spread. When a live database is given the
    /// two spreads are compared; they differ legitimately if lines changed since the copy was made.
    /// </summary>
    public static VerifyResult Verify(string backupFile, string? liveConnectionString, int year)
    {
        if (!File.Exists(backupFile)) throw new FileNotFoundException($"'{backupFile}' does not exist.");
        var backupConnection = new SqliteConnectionStringBuilder { DataSource = backupFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();

        string integrity;
        try
        {
            using var conn = new SqliteConnection(backupConnection);
            conn.Open();
            integrity = conn.ExecuteScalar<string>("PRAGMA integrity_check") ?? "no answer";
        }
        catch (SqliteException e)
        {
            integrity = e.Message; // not a database at all, or cut short
        }
        if (integrity != "ok")
            return new VerifyResult(false, integrity, year, new Spread(0, 0, 0, 0, 0, 0), null, false, $"The copy is damaged: {integrity}");

        var backup = YearSpread(backupConnection, year);
        Spread? live = liveConnectionString is null ? null : YearSpread(liveConnectionString, year);
        var same = live is not null && live == backup;
        var message = live is null
            ? "The copy opens and passes the integrity check."
            : same
                ? $"The copy opens, passes the integrity check, and adds up to the same {year} spread as the live database."
                : $"The copy opens and passes the integrity check, but its {year} spread differs from the live database. That is expected if lines changed since it was made.";
        return new VerifyResult(true, integrity, year, backup, live, same, message);
    }

    /// <summary>
    /// Puts a copy back as the database at <paramref name="targetConnectionString"/> (and its keys into <paramref name="keysDir"/>).
    /// Refuses to replace a database that is there unless told to, and checks the result before reporting success.
    /// </summary>
    public static RestoreResult Restore(string backupFile, string targetConnectionString, string? keysTar, string? keysDir, bool replace, int year)
    {
        var before = Verify(backupFile, null, year);
        if (!before.Ok) throw new InvalidDataException(before.Message);

        var target = Path.GetFullPath(new SqliteConnectionStringBuilder(targetConnectionString).DataSource);
        if (File.Exists(target) && !replace) throw new InvalidDataException($"{target} already exists. Use --force to replace it.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(target + suffix)) File.Delete(target + suffix);
        File.Copy(backupFile, target);
        SqliteConnection.ClearAllPools();

        string? restoredKeys = null;
        if (keysTar is not null)
        {
            if (keysDir is null) throw new InvalidDataException("Say where the keys go.");
            if (!File.Exists(keysTar)) throw new FileNotFoundException($"'{keysTar}' does not exist.");
            Directory.CreateDirectory(keysDir);
            TarFile.ExtractToDirectory(keysTar, keysDir, overwriteFiles: replace);
            restoredKeys = keysDir;
        }

        var check = Verify(target, targetConnectionString, year);
        return new RestoreResult(target, restoredKeys, check);
    }
}
