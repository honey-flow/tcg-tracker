using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public class SchemaBootstrap
{
  private readonly Database _db;
  private readonly string _schemaPath;

  public SchemaBootstrap(Database db, string schemaFileName = "Schema_v00.sql")
  {
    _db = db;
    _schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", schemaFileName);
  }

  private const int ExpectedVersion = 0;

  public void EnsureCreated()
  {
    using var conn = _db.Connect();

    var current = GetUserVersion(conn);

    if (TablesExist(conn))
    {
      if(current != ExpectedVersion)
        throw new InvalidOperationException(
          $"Database is at schema version {current}, code expects " +
          $"{ExpectedVersion}. A migration is needed.");

      return;
    }

    if (!File.Exists(_schemaPath))
      throw new FileNotFoundException(
        $"Schema file not found at {_schemaPath}. Check that the " +
        $"<None Update=\"Data\\*.sql\" CopyToOutputDirectory> entry " +
        $"is in the .csproj and that file is in Data/.", _schemaPath
      );

    var sql = File.ReadAllText(_schemaPath);

    using var tx = conn.BeginTransaction();

    using (var cmd = conn.CreateCommand())
    {
      cmd.Transaction = tx;
      cmd.CommandText = sql;
      cmd.ExecuteNonQuery();
    }

    tx.Commit();
  }

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