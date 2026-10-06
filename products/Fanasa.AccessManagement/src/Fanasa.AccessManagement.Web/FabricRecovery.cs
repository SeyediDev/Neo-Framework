using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.Persistence;

public sealed record RecoveryFile(string Name, long Length, string Sha256);
public sealed record RecoveryManifest(int Format, DateTimeOffset CreatedAt, RecoveryFile[] Files);

/// <summary>Administrative offline bundle recovery. Never overwrites existing data.</summary>
public static class FabricRecovery
{
    private static readonly string[] Names = ["fabric.db", "platform-registry.db"];
    public static RecoveryManifest Backup(string fabricPath, string registryPath, string destination, bool offlineConfirmed)
    {
        if (!offlineConfirmed) throw new ArgumentException("Stop all Access writers and explicitly confirm offline mode.");
        var sources = new[] { Path.GetFullPath(fabricPath), Path.GetFullPath(registryPath) };
        if (string.Equals(sources[0], sources[1], StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Two independent source databases are required.");
        for (var i = 0; i < sources.Length; i++) ValidateDatabase(sources[i], Names[i]);
        var target = NewTarget(destination); var stage = CreateStage(target);
        for (var i = 0; i < Names.Length; i++)
        {
            using var source = Open(sources[i], SqliteOpenMode.ReadOnly);
            using var copy = Open(Path.Combine(stage, Names[i]), SqliteOpenMode.ReadWriteCreate);
            source.BackupDatabase(copy);
            using var mode = copy.CreateCommand(); mode.CommandText = "PRAGMA journal_mode=DELETE;"; mode.ExecuteScalar();
        }
        var manifest = new RecoveryManifest(1, DateTimeOffset.UtcNow, Names.Select(name => Describe(Path.Combine(stage, name), name)).ToArray());
        using (var file = new FileStream(Path.Combine(stage, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, manifest); file.Flush(true); }
        Verify(stage);
        Directory.Move(stage, target);
        return manifest;
    }
    public static RecoveryManifest Verify(string bundle)
    {
        bundle = Path.GetFullPath(bundle); RejectLinks(bundle);
        var manifestPath = Path.Combine(bundle, "manifest.json"); RejectLinks(manifestPath);
        if (new FileInfo(manifestPath).Length > 64 * 1024) throw new InvalidDataException("Manifest is too large.");
        var manifest = JsonSerializer.Deserialize<RecoveryManifest>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("Invalid manifest.");
        if (manifest.Format != 1 || manifest.Files is null || manifest.Files.Length != 2 || manifest.Files.Any(x => x is null)
            || !manifest.Files.Select(x => x.Name).Order().SequenceEqual(Names.Order())) throw new InvalidDataException("Unknown recovery format or file set.");
        if (!Directory.GetFileSystemEntries(bundle).Select(Path.GetFileName).Order().SequenceEqual(Names.Append("manifest.json").Order()))
            throw new InvalidDataException("Unexpected files in recovery bundle.");
        foreach (var entry in manifest.Files)
        {
            var path = Path.Combine(bundle, entry.Name); RejectLinks(path);
            if (Describe(path, entry.Name) != entry) throw new InvalidDataException("Recovery checksum mismatch.");
            ValidateDatabase(path, entry.Name);
        }
        return manifest;
    }
    public static RecoveryManifest Restore(string bundle, string destination, bool offlineConfirmed)
    {
        if (!offlineConfirmed) throw new ArgumentException("Stop all Access writers and explicitly confirm offline mode.");
        var manifest = Verify(bundle); var target = NewTarget(destination);
        var bundleRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundle));
        if (target.StartsWith(bundleRoot + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Restore destination cannot be inside the backup bundle.");
        var stage = CreateStage(target);
        foreach (var entry in manifest.Files)
        {
            using var source = new FileStream(Path.Combine(bundle, entry.Name), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var copy = new FileStream(Path.Combine(stage, entry.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(copy); copy.Flush(true);
        }
        using (var file = new FileStream(Path.Combine(stage, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, manifest); file.Flush(true); }
        // Recheck copies to detect source changes between verification and copy.
        Verify(stage); Directory.Move(stage, target); return manifest;
    }
    private static RecoveryFile Describe(string path, string name)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(name, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    private static void ValidateDatabase(string path, string name)
    {
        RejectLinks(path); if (!File.Exists(path)) throw new FileNotFoundException("Recovery source is missing.", path);
        using var connection = Open(path, SqliteOpenMode.ReadOnly); using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using (var reader = command.ExecuteReader())
        { if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read()) throw new InvalidDataException("Database integrity check failed."); }
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = command.ExecuteReader()) { if (reader.Read()) throw new InvalidDataException("Foreign key check failed."); }
        command.CommandText = name == "fabric.db"
            ? "SELECT kind,key,json FROM fabric_documents LIMIT 0; SELECT id,key,kind,payload,recorded FROM fabric_outbox LIMIT 0;"
            : "SELECT id,name,namespace FROM platform_tenants LIMIT 0; SELECT subject,tenant,permission,expires FROM platform_grants LIMIT 0; SELECT id,key,data FROM platform_products LIMIT 0; SELECT id,actor,operation,data,recorded FROM platform_audit LIMIT 0; SELECT key,value FROM platform_meta LIMIT 0;";
        using var schema = command.ExecuteReader(); while (schema.NextResult()) { }
    }
    private static string NewTarget(string destination)
    {
        var target = Path.GetFullPath(destination); RejectLinks(Path.GetDirectoryName(target)!);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Destination already exists; recovery never overwrites data.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("Destination parent must exist.");
        return target;
    }
    private static string CreateStage(string target)
    {
        var stage = target + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage); RejectLinks(stage);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return stage;
    }
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery paths cannot traverse symbolic links or junctions.");
    }
}
