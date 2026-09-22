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

    var stored = _repo.LoadAllKeys(conn);
    var bySourceId = _repo.LoadSourceIds(conn);

    var toInsert = new List<Printing>();
    var toUpdate = new List<(long Id, Printing P)>();
    var discrepancies = new List<Discrepancy>();
    var renames = new List<Rename>();
    var unchanged = 0;
    var duplicates = 0;
    var seen = new HashSet<PrintingKey>();

    foreach (var p in printings)
    {
      var key = PrintingRepository.KeyOf(p);

      if (!seen.Add(key))
      {
        duplicates++;
        continue;
      }

      if (stored.TryGetValue(key, out var existing))
      {
        var changes = Diff(existing, p);

        if (changes.Count == 0)
        {
          unchanged++;
          continue;
        }

        discrepancies.Add(new Discrepancy(key, existing.PrintingId, changes));
        toUpdate.Add((existing.PrintingId, p));
        continue;
      }
      if (p.SourceName is not null && p.SourceId is not null 
          && bySourceId.TryGetValue((p.SourceName, p.SourceId, p.Finish), out var movedId))
      {
        renames.Add(new Rename(key, movedId, p.SourceName, p.SourceId));
        continue;
      }
      toInsert.Add(p);
    }
    using (var tx = conn.BeginTransaction())
    {
      foreach (var p in toInsert) _repo.Insert(conn, p, tx);
      foreach (var (id, p) in toUpdate) _repo.Update(conn, id, p, tx);

      tx.Commit();
    }

    return new ImportResult(toInsert.Count, toUpdate.Count, unchanged, duplicates, discrepancies, renames);
  }

  private static List<FieldChange> Diff(StoredPrinting s, Printing p)
  {
    var changes = new List<FieldChange>();

    Compare("name",          s.Name,                     p.Name);
    Compare("card_type",     s.CardType,                 p.CardType);
    Compare("rarity",        s.Rarity,                   p.Rarity);
    Compare("image_uri",     s.ImageUri,                 p.ImageUri);
    Compare("source_id",     s.SourceId,                 p.SourceId);
    Compare("source_name",   s.SourceName,               p.SourceName);
    Compare("tcgplayer_id",  s.TcgplayerId?.ToString(),  p.TcgplayerId?.ToString());
    Compare("cardmarket_id", s.CardmarketId?.ToString(), p.CardmarketId?.ToString());

    return changes;

    void Compare(string field, string? stored, string? incoming)
    {
      if (stored != incoming)
        changes.Add(new FieldChange(field, stored, incoming));
    }
  }

}