using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public record FieldChange(string Field, string? Stored, string? Incoming);

public record Discrepancy(PrintingKey Key, long PrintingId, IReadOnlyList<FieldChange> Changes);

public record Rename(PrintingKey IncomingKey, long ExistingPrintingId, string SourceName, string SourceId);

public record ImportResult(int Inserted, int Updated, int Unchanged, int Duplicates, IReadOnlyList<Discrepancy> Discrepancies, IReadOnlyList<Rename> Renames)
{
  public int Total => Inserted + Updated + Unchanged + Duplicates + Renames.Count;
}

public class CatalogImporter
{
  private readonly Database _db;
  private readonly PrintingRepository _repo;

  public CatalogImporter(Database db, PrintingRepository repo)
  {
    _db = db;
    _repo = repo;
  }

  public ImportResult Import(IEnumerable<Printing> printings)
  {
    using var conn = _db.Connect();
    var run = new ImportRun(_repo.LoadAllKeys(conn), _repo.LoadSourceIds(conn));
    
    foreach (var p in printings) run.Classify(p);
    run.Settle();

    WriteBatch(conn, run.ToInsert, run.ToUpdate);

    return run.Result(run.ToInsert.Count, run.ToUpdate.Count);
  }

  public async Task<ImportResult> ImportBulkAsync(IAsyncEnumerable<Printing> printings, int batchSize = 5_000, Action<string>? progress = null)
  {
    using var conn = _db.Connect();

    progress?.Invoke("  reading existing printings...");
    var run = new ImportRun(_repo.LoadAllKeys(conn), _repo.LoadSourceIds(conn));
    progress?.Invoke($"  {run.StoredCount:N0} printings already stored");

    var inserted   = 0; 
    var updated    = 0; 
    var processed  = 0;

    await foreach (var p in printings)
    {
      processed++;
      run.Classify(p);

      if (run.ToInsert.Count + run.ToUpdate.Count >= batchSize)
      {
        WriteBatch(conn, run.ToInsert, run.ToUpdate);
        inserted += run.ToInsert.Count;
        updated  += run.ToUpdate.Count;
        run.ToInsert.Clear();
        run.ToUpdate.Clear();
      }

      if (processed % 10_000 == 0) progress?.Invoke($" {processed:N0} processed...");
    }

    run.Settle();

    if (run.ToInsert.Count + run.ToUpdate.Count > 0)
    {
      WriteBatch(conn, run.ToInsert, run.ToUpdate);
      inserted += run.ToInsert.Count;
      updated  += run.ToUpdate.Count;
    }     

    return run.Result(inserted, updated);
  }

  private sealed class ImportRun
  {
    private readonly Dictionary<PrintingKey, StoredPrinting> _stored;
    private readonly Dictionary<(string Source, string Id, string Finish), List<long>> _bySourceId;
    private readonly HashSet<PrintingKey> _seen = new();

    private readonly HashSet<long> _matched = new();

    private readonly List<(Printing P, PrintingKey Key, (string Source, string Id, string Finish) SourceKey)> _candidates = new();

    private readonly List<Discrepancy> _discrepancies = new();
    private readonly List<Rename>      _renames       = new();
    private int _unchanged;
    private int _duplicates;

    public List<Printing>              ToInsert {get;} = new();
    public List<(long Id, Printing P)> ToUpdate {get;} = new();
    public int StoredCount => _stored.Count;

    public ImportRun(Dictionary<PrintingKey, StoredPrinting> stored, Dictionary<(string Source, string Id, string Finish), List<long>> bySourceId)
    {
      _stored     = stored;
      _bySourceId = bySourceId;
    }

    public void Classify(Printing p)
    {
      var key = PrintingRepository.KeyOf(p);

      if (!_seen.Add(key)) 
        { 
          _duplicates++; 
          return; 
        }

      if (_stored.TryGetValue(key, out var existing))
      {
        _matched.Add(existing.PrintingId);

        var changes = Diff(existing, p);
        if (changes.Count == 0)
        {
          _unchanged++;
          return;
        }

        _discrepancies.Add(new Discrepancy(key, existing.PrintingId, changes));
        ToUpdate.Add((existing.PrintingId, p));
        return;
      }

      if (p.SourceName is string source && p.SourceId is string id)
      {
        var sk = (Source: source, Id: id, Finish: p.Finish);
        if (_bySourceId.ContainsKey(sk))
        {
          _candidates.Add((p, key, sk));
          return;
        }
      }

      ToInsert.Add(p);
    }
    
    public void Settle()
    {
      foreach (var group in _candidates.GroupBy(c => c.SourceKey))
      {
        var storedIds = _bySourceId[group.Key];
        var unmatched = storedIds.Count(id => !_matched.Contains(id));
        var claims    = group.ToList();
        if (storedIds.Count == 1 && unmatched == 1 && claims.Count == 1)
        {
          _renames.Add(new Rename(claims[0].Key, storedIds[0], group.Key.Source, group.Key.Id));
        }
        else
        {
          foreach (var c in claims) ToInsert.Add(c.P);
        }
      }

      _candidates.Clear();
    }

    public ImportResult Result(int inserted, int updated) =>
      new(inserted, updated, _unchanged, _duplicates, _discrepancies, _renames);
  }

  private void WriteBatch(SqliteConnection conn, List<Printing> toInsert, List<(long Id,Printing P)> toUpdate)
  {
    using var tx = conn.BeginTransaction();
    foreach (var p in toInsert)      _repo.Insert(conn, p, tx);
    foreach (var (id,p) in toUpdate) _repo.Update(conn, id, p, tx);

    tx.Commit();
  }

  private static List<FieldChange> Diff(StoredPrinting s, Printing p)
  {
    var changes = new List<FieldChange>();

    Compare("name",              s.Name,            p.Name);
    Compare("card_type",         s.CardType,        p.CardType);
    Compare("rarity",            s.Rarity,          p.Rarity);
    Compare("image_uri",         s.ImageUri,        p.ImageUri);
    Compare("mtg_frame_effects", s.MtgFrameEffects, p.MtgFrameEffects);
    CompareIfPresent("source_id",     s.SourceId,                 p.SourceId);
    CompareIfPresent("source_name",   s.SourceName,               p.SourceName);
    CompareIfPresent("tcgplayer_id",  s.TcgplayerId?.ToString(),  p.TcgplayerId?.ToString());
    CompareIfPresent("cardmarket_id", s.CardmarketId?.ToString(), p.CardmarketId?.ToString());

    return changes;

    void Compare(string field, string? stored, string? incoming)
    {
      if (stored != incoming)
        changes.Add(new FieldChange(field, stored, incoming));
    }

    void CompareIfPresent(string field, string? stored, string? incoming)
    {
      if (incoming is not null)
        Compare(field, stored, incoming);
    }
  }

}