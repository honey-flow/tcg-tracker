using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public record VariantCollision(string Game, string SourceName, string SourceCardId, int Count);

public class CollisionRepository
{
  public void Record(SqliteConnection conn, VariantCollision c, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      INSERT OR IGNORE INTO variant_collisions
        (game, source_name, source_card_id, collision_count)
      VALUES ($game, $source_name, $source_card_id, $collision_count);";

    cmd.Parameters.AddWithValue("$game",            c.Game        );
    cmd.Parameters.AddWithValue("$source_name",     c.SourceName  );
    cmd.Parameters.AddWithValue("$source_card_id",  c.SourceCardId);
    cmd.Parameters.AddWithValue("$collision_count", c.Count       );

    cmd.ExecuteNonQuery();
  }

  public List<VariantCollision> GetPending(SqliteConnection conn, string? sourceName = null, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      SELECT game, source_name, source_card_id, collision_count
      FROM variant_collisions
      WHERE ($source IS NULL OR source_name = $source)
        AND resolved_at IS NULL
      ORDER BY detected_at, source_card_id;";
    cmd.Parameters.AddWithValue("$source", (object?)sourceName ?? DBNull.Value);

    var list = new List<VariantCollision>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      list.Add(new VariantCollision(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
    return list;
  }

  public void MarkResolved(SqliteConnection conn, string sourceName, string sourceCardId, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      UPDATE variant_collisions SET resolved_at = datetime('now')
      WHERE source_name = $source_name AND source_card_id = $source_card_id;";

    cmd.Parameters.AddWithValue("$source_name",    sourceName  );
    cmd.Parameters.AddWithValue("$source_card_id", sourceCardId);

    cmd.ExecuteNonQuery();
  }

  public int CountPending(SqliteConnection conn, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      SELECT COUNT(*) FROM variant_collisions;";
    return Convert.ToInt32(cmd.ExecuteScalar());
  }
}
