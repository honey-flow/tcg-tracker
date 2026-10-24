using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public record PriceObservation(
  long   PrintingId,
  string Source,
  string Metric,
  string Currency,
  long   AmountCents,
  string AsOf
);

public record PriceSnapshot(
  long   PrintingId,
  string Source,
  string AsOf,
  string Payload
);

public record PriceWriteResult(
  int PrintingsCovered,
  int CurrentRowsWritten,
  int SnapshotsAdded,
  int SnapshotsAlreadyStored
);

public class PriceRepository
{
  public PriceWriteResult Write(SqliteConnection conn, 
                                string source, 
                                IReadOnlyCollection<long> covered,
                                IReadOnlyCollection<PriceObservation> current,
                                IReadOnlyCollection<PriceSnapshot> snapshots,
                                string fetchedAt)
  {
    var coveredSet = covered.ToHashSet();
    foreach (var o in current)
    {
      if (o.Source != source)
        throw new ArgumentException($"Observation for printing {o.PrintingId} is from '{o.Source}', not '{source}'");

      if (!coveredSet.Contains(o.PrintingId))
        throw new ArgumentException($"Observation for printing {o.PrintingId} ({o.Metric}) is not in the covered set");
    }

    foreach (var s in snapshots)
      if (s.Source != source)
        throw new ArgumentException($"Snapshot for printing {s.PrintingId} is from '{s.Source}', not '{source}'");

    var dup = current.GroupBy(o => (o.PrintingId, o.Metric)).FirstOrDefault(g => g.Count() > 1);
    if (dup is not null)
      throw new InvalidOperationException($"Printing {dup.Key.PrintingId} has {dup.Count()} values for '{dup.Key.Metric}' from {source}");

    using var tx = conn.BeginTransaction();

    using (var del = conn.CreateCommand())
    {
      del.Transaction = tx;
      del.CommandText = @"
                          DELETE FROM current_prices
                          WHERE printing_id = $printing_id AND source = $source;";

      var pId = del.Parameters.Add("$printing_id", SqliteType.Integer);
      del.Parameters.AddWithValue("$source", source);

      foreach (var id in coveredSet)
      {
        pId.Value = id;
        del.ExecuteNonQuery();
      }
    }

    using (var ins = conn.CreateCommand())
    {
      ins.Transaction = tx;
      ins.CommandText = @"
                          INSERT INTO current_prices
                            (printing_id, source, metric, currency, amount_cents, as_of, fetched_at)
                          VALUES
                            ($printing_id, $source, $metric, $currency, $amount_cents, $as_of, $fetched_at);";

      var pId       = ins.Parameters.Add("$printing_id",  SqliteType.Integer);
      var pMetric   = ins.Parameters.Add("$metric",       SqliteType.Text);
      var pCurrency = ins.Parameters.Add("$currency",     SqliteType.Text);
      var pAmount  = ins.Parameters.Add("$amount_cents", SqliteType.Integer);
      var pAsOf     = ins.Parameters.Add("$as_of",        SqliteType.Text);
      ins.Parameters.AddWithValue("$source",     source);
      ins.Parameters.AddWithValue("$fetched_at", fetchedAt);

      foreach (var o in current)
      {
        pId.Value        = o.PrintingId;
        pMetric.Value    = o.Metric;          
        pCurrency.Value  = o.Currency;
        pAmount.Value    = o.AmountCents;
        pAsOf.Value      = o.AsOf;
        ins.ExecuteNonQuery();
      }
    }

    var added = 0;
    using (var snap = conn.CreateCommand())
    {
      snap.Transaction = tx;
      snap.CommandText = @"
                           INSERT INTO price_snapshots 
                             (printing_id, source, as_of, payload, fetched_at)
                           VALUES 
                             ($printing_id, $source, $as_of, $payload, $fetched_at)
                           ON CONFLICT 
                             (printing_id, source, as_of) DO NOTHING;";

      var pId     = snap.Parameters.Add("$printing_id", SqliteType.Integer);
      var pAsOf    = snap.Parameters.Add("$as_of",       SqliteType.Text);
      var pPayload = snap.Parameters.Add("$payload",     SqliteType.Text);
      snap.Parameters.AddWithValue("$source",     source);
      snap.Parameters.AddWithValue("$fetched_at", fetchedAt);

      foreach (var s in snapshots)
      {
        pId.Value      = s.PrintingId;
        pAsOf.Value    = s.AsOf;
        pPayload.Value = s.Payload;

        added += snap.ExecuteNonQuery();
      }
    }

    tx.Commit();
    return new PriceWriteResult(coveredSet.Count, current.Count, added, snapshots.Count - added);
  }

  public HashSet<long> LoadHeldPrintingIds(SqliteConnection conn, string game, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
                        SELECT DISTINCT c.printing_id
                        FROM copies c
                        JOIN printings p ON p.printing_id = c.printing_id
                        WHERE p.game = $game
                          AND c.status IN ('owned', 'at_grader');";
    cmd.Parameters.AddWithValue("$game", game);

    var ids = new HashSet<long>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) ids.Add(r.GetInt64(0));
    return ids;
  }

  public List<PriceObservation> GetCurrent(SqliteConnection conn, long printingId, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
                        SELECT printing_id, source, metric, currency, amount_cents, as_of
                        FROM current_prices
                        WHERE printing_id = $printing_id
                        ORDER BY source, metric;";
    cmd.Parameters.AddWithValue("$printing_id", printingId);

    var list = new List<PriceObservation>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
      list.Add(new PriceObservation(
        r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetInt64(4), r.GetString(5)));

    return list;
  }
}