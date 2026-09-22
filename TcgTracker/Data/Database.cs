using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public class Database
{
  private readonly string _connectionString;

  public Database(string dbPath)
  {
    _connectionString = new SqliteConnectionStringBuilder{DataSource = dbPath}.ToString();
  }

  public SqliteConnection Connect()
  {
   var conn = new SqliteConnection(_connectionString);
   conn.Open();

   using var pragma = conn.CreateCommand();
   pragma.CommandText = "PRAGMA foreign_keys = ON;";
   pragma.ExecuteNonQuery();

   return conn;   
  }
}