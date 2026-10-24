using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public record CatalogIssue(
  string  Game,
  string  SourceName, 
  string  Kind,
  string  IssueKey,
  string  Summary,
  string? Detail  
);

public record StoredIssue(
  long         IssueId,
  CatalogIssue Issue,
  string       FirstSeenAt,
  string       LastSeenAt
);

public record IssueRunResult(
  int Seen, 
  int Resolved
);

public class CatalogIssueRepository
{
  public static string NewRunStamp() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

  public static string IssueKeyOf(PrintingKey k) =>
    string.Join("|", k.Game, k.SetCode, k.CollectorNumber, 
                     k.Finish, k.PrintVariant, k.VariantKey,
                     k.PkmnPromoStamp, k.OpParallelType);

  public IssueRunResult RecordRun(SqliteConnection conn, string game, string sourceName, IReadOnlyCollection<CatalogIssue> issues, string runAt)
  {
    foreach (var i in issues)
    {
      if (i.Game != game || i.SourceName != sourceName)
        throw new ArgumentException($"Issue {i.Kind}:{i.IssueKey} is for {i.Game}/{i.SourceName}, not {game}/{sourceName}");

      if (i.Kind == "limitation")
        throw new ArgumentException($"Imports may not write limitation issues ({i.IssueKey})");
    }

    var dup = issues.GroupBy(i => (i.Kind, i.IssueKey)).FirstOrDefault(g => g.Count() > 1);
    if (dup is not null)
      throw new InvalidOperationException($"Duplicate issues in one run: {dup.Key.Kind}:{dup.Key.IssueKey} x{dup.Count()}");
    
    using var tx = conn.BeginTransaction();

    using (var up = conn.CreateCommand())
    {
      up.Transaction = tx;

      up.CommandText = @"
        INSERT INTO catalog_issues
          (game, source_name, kind, issue_key, summary, detail, first_seen_at, last_seen_at)
        VALUES
          ($game, $source_name, $kind, $issue_key, $summary, $detail, $run_at, $run_at)
        ON CONFLICT (game, source_name, kind, issue_key) DO UPDATE SET
          summary      = excluded.summary,
          detail       = excluded.detail,
          last_seen_at = excluded.last_seen_at,
          resolved_at  = NULL;";

      var pGame    = up.Parameters.Add("$game",        SqliteType.Text);
      var pSource  = up.Parameters.Add("$source_name", SqliteType.Text);
      var pKind    = up.Parameters.Add("$kind",        SqliteType.Text);
      var pKey     = up.Parameters.Add("$issue_key",   SqliteType.Text);
      var pSummary = up.Parameters.Add("$summary",     SqliteType.Text);
      var pDetail  = up.Parameters.Add("$detail",      SqliteType.Text);
      var pRunAt   = up.Parameters.Add("$run_at",      SqliteType.Text);

      foreach (var i in issues)
      {
        pGame.Value    = i.Game;
        pSource.Value  = i.SourceName;
        pKind.Value    = i.Kind;
        pKey.Value     = i.IssueKey;
        pSummary.Value = i.Summary;
        pDetail.Value  = (object?)i.Detail ?? DBNull.Value;
        pRunAt.Value   = runAt;
        up.ExecuteNonQuery();
      }
    }

    int resolved;
    using (var res = conn.CreateCommand())
    {
      res.Transaction = tx;

      res.CommandText = @"
        UPDATE catalog_issues SET resolved_at = $run_at
        WHERE game         = $game
          AND source_name  = $source_name
          AND kind         <> 'limitation'
          AND resolved_at  IS NULL
          AND last_seen_at <> $run_at;";

      res.Parameters.AddWithValue("$game",        game);
      res.Parameters.AddWithValue("$source_name", sourceName);
      res.Parameters.AddWithValue("$run_at",      runAt);

      resolved = res.ExecuteNonQuery();
    }

    tx.Commit();
    return new IssueRunResult(issues.Count, resolved);
  }

  public List<StoredIssue> GetOpen(SqliteConnection conn, string? game = null, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      SELECT issue_id, game, source_name, kind, issue_key, summary, detail, first_seen_at, last_seen_at
      FROM catalog_issues
      WHERE resolved_at IS NULL
        AND ($game IS NULL OR game = $game)
      ORDER BY game,
               CASE kind
                WHEN 'held_out'   THEN 1
                WHEN 'stale'      THEN 2
                WHEN 'limitation' THEN 3
                WHEN 'merged'     THEN 4
                WHEN 'corrected'  THEN 5
               END,
               issue_key;";
    cmd.Parameters.AddWithValue("$game", (object?)game ?? DBNull.Value);

    var list = new List<StoredIssue>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
      list.Add(new StoredIssue(
        r.GetInt64(0),
        new CatalogIssue(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), 
                         r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)),
        r.GetString(7), r.GetString(8)));

    return list;
  }
}