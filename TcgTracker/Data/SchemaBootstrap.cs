using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public class SchemaBootstrap
{
  private readonly Database _db;
  private readonly string _schemaPath;

  private const int ExpectedVersion = 0;

  public SchemaBootstrap(Database db, string schemaFileName = "Schema_v00.sql")
  {
    _db = db;
    _schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", schemaFileName);
  }

  public void EnsureCreated()
  {
    var sql = LoadSchemaText();
    var fingerprint = Fingerprint(sql);

    using var conn = _db.Connect();

    if (TablesExist(conn))
    {
      var current = GetUserVersion(conn);
      if(current != ExpectedVersion)
        throw new InvalidOperationException(
          $"Database is at schema version {current}, code expects " +
          $"{ExpectedVersion}. A migration is needed.");

      var stored = ReadStoredFingerprint(conn);

      if (stored is null)
        throw StaleDatabase("has no schema fingerprint");

      if (!string.Equals(stored, fingerprint, StringComparison.Ordinal))
        throw StaleDatabase(
          $"was built from a different Schema_v00.sql " +
          $"(built from {stored[..12]}..., file is now {fingerprint[..12]}...)");

      return;
    }

    using var tx = conn.BeginTransaction();

    using (var cmd = conn.CreateCommand())
    {
      cmd.Transaction = tx;
      cmd.CommandText = sql;

      cmd.ExecuteNonQuery();
    }

    using (var meta = conn.CreateCommand())
    {
      meta.Transaction = tx;
      meta.CommandText = @"
        INSERT INTO schema_meta (schema_meta_id, schema_sha256, created_at)
        VALUES (1, $schema_sha256, datetime('now'));";
      
      meta.Parameters.AddWithValue("$schema_sha256", fingerprint);
      meta.ExecuteNonQuery();
    }

    tx.Commit();
  }

  private string LoadSchemaText()
  {
    if (!File.Exists(_schemaPath))
      throw new FileNotFoundException(
        $"Schema file not found at {_schemaPath}. Check that the " +
        $"<None Update=\"Data\\*.sql\" CopyToOutputDirectory> entry " +
        $"is in the .csproj and that file is in Data/.", _schemaPath);

    return File.ReadAllText(_schemaPath);
  }

  private static string Fingerprint(string sql)
  {
    var normalised = sql.Replace("\r\n", "\n");
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
    return Convert.ToHexString(hash);
  }

  private static string? ReadStoredFingerprint(SqliteConnection conn)
  {
    using (var exists = conn.CreateCommand())
    {
      exists.CommandText = @"
        SELECT COUNT(*) FROM sqlite_master  WHERE type = 'table' AND name = 'schema_meta';";
      if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return null;
    }

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT schema_sha256 FROM schema_meta WHERE schema_meta_id = 1;";
    return cmd.ExecuteScalar() as string;
  }

  private InvalidOperationException StaleDatabase(string problem) => 
    new($"The database at {_db.DbPath} {problem}. " +
        $"Delete it (and any -wal/-shm files beside it) and run again to rebuild from the current schema.");

  private static bool TablesExist(SqliteConnection conn)
  {
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'printings';";
    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
  }

  private static int GetUserVersion(SqliteConnection conn)
  {
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA user_version;";
    return Convert.ToInt32(cmd.ExecuteScalar());
  }
}