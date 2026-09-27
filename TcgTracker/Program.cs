using System.Net;
using Microsoft.Data.Sqlite;
using TcgTracker.Data;

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TcgTracker");
Directory.CreateDirectory(dataDir);
var db = new Database(Path.Combine(dataDir, "tcg.db"));
new SchemaBootstrap(db).EnsureCreated();

/**
using (var conn = db.Connect())
{
  using var check = conn.CreateCommand();
  check.CommandText =
    "SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'), " +
    "       (SELECT COUNT(*) FROM locations)";

  using var r = check.ExecuteReader();
  r.Read();
  Console.WriteLine($"Database ready: {r.GetInt32(0)} tables, {r.GetInt32(1)} locations");

  using var fk = conn.CreateCommand();
  fk.CommandText = "PRAGMA foreign_keys";
  Console.WriteLine($"FK enforcement: {fk.ExecuteScalar()}");
}
**/

var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All} );
http.DefaultRequestHeaders.Add("User-Agent", "TcgTracker/0.1");
http.DefaultRequestHeaders.Add("Accept", "application/json");

var sources = new Dictionary<string, ICatalogSource>
{
  ["mtg"]      = new ScryfallSource(http),
  ["pokemon"]  = new TcgdexSource(http),
  ["onepiece"] = new OptcgSource(http)
};

var refs = new[]
{
  new CardRef("mtg",      "lea",   "161"),
  new CardRef("pokemon",  "swsh3", "136"),
  new CardRef("onepiece", "OP01",  "001"),
  new CardRef("pokemon",  "base1", "4"),
  new CardRef("mtg",      "isd",   "51")
};

var printings = new List<Printing>();
foreach (var cardRef in refs)
{
  var got = (await sources[cardRef.Game].GetPrintingAsync(cardRef)).ToList();

  if (got.Count == 0)
    Console.WriteLine($"  WARNING  {cardRef.Game} {cardRef.SetCode} {cardRef.Number} " +
                      $"returned nothing -- source failed or card not found");
  
  printings.AddRange(got);
}

Console.WriteLine($"Fetched {printings.Count} printings.");

var importer = new CatalogImporter(db, new PrintingRepository());
var result = importer.Import(printings);

Console.WriteLine($"Inserted {result.Inserted}, updated {result.Updated}, unchanged {result.Unchanged}, duplicates {result.Duplicates}, renames {result.Renames.Count}  (total {result.Total})");
if (result.Total != printings.Count) Console.WriteLine($"  COUNT MISMATCH: fetched {printings.Count}, accounted for {result.Total}");

foreach (var d in result.Discrepancies)
{
  Console.WriteLine($"CHANGED {d.Key.Game} {d.Key.SetCode} {d.Key.CollectorNumber} {d.Key.Finish}");
  foreach (var c in d.Changes)
    Console.WriteLine($"        {c.Field}: '{c.Stored}' -> '{c.Incoming}'");
}

foreach (var r in result.Renames)
{
  Console.WriteLine(
    $"  RENAME?   {r.SourceName}:{r.SourceId} now keys as " + 
    $"{r.IncomingKey.SetCode} {r.IncomingKey.CollectorNumber} " +
    $"(existing printing_id {r.ExistingPrintingId}) - NOT applied");
}

const bool runBulkImport = true;

if (runBulkImport)
{
  var bulk = new ScryfallBulk(http, Path.Combine(dataDir, "bulk"));
  var bulkFile = await bulk.EnsureFileAsync(Console.WriteLine);

  var sw = System.Diagnostics.Stopwatch.StartNew();

  var bulkResult = await importer.ImportBulkAsync(
    ScryfallBulk.StreamPrintingsAsync(bulkFile),
    batchSize: 5_000, 
    progress: Console.WriteLine);
  
  sw.Stop();
  
  Console.WriteLine(
    $"Bulk: inserted {bulkResult.Inserted:N0}, updated {bulkResult.Updated:N0}, " +
    $"unchanged {bulkResult.Unchanged:N0}, duplicates {bulkResult.Duplicates:N0}, " +
    $"renames {bulkResult.Renames.Count:N0} in {sw.Elapsed.TotalSeconds:N1}s");
}

// Location Repo Test - Does not Insert into the real Database
var locRepo = new LocationRepository();
using (var conn = db.Connect())
using (var tx = conn.BeginTransaction())
{
  var cabA = locRepo.Insert(conn, new Location { Name = "Cabinet A" }, tx);
  var cabB = locRepo.Insert(conn, new Location { Name = "Cabinet B" }, tx);
  Console.WriteLine($"Cabinet A : {cabA.Outcome} id {cabA.locationId}");

  // Same name, different parents
  var d1 = locRepo.Insert(conn, new Location { Name = "Drawer 1", ParentLocationId = cabA.locationId }, tx);
  var d2 = locRepo.Insert(conn, new Location { Name = "Drawer 1", ParentLocationId = cabB.locationId }, tx);
  Console.WriteLine($"Drawer 1 in A: {d1.locationId}; Drawer 1 in B: {d2.locationId}");

  // Same name, same parent -- reported, not thrown, with the existing id
  var dup = locRepo.Insert(conn, new Location { Name = "Drawer 1", ParentLocationId = cabA.locationId }, tx);
  Console.WriteLine($"Duplicate sibling: {dup.Outcome}, id {dup.locationId} (should be {d1.locationId})");

  // Duplicate TOP-LEVEL name
  var dupTop = locRepo.Insert(conn, new Location { Name = "Cabinet A" }, tx);
  Console.WriteLine($"Duplicate top level: {dupTop.Outcome}, id {dupTop.locationId} (should be {cabA.locationId})");

  Console.WriteLine($"Top-level location: {locRepo.GetChildren(conn, null, tx).Count}");
  Console.WriteLine($"Inside Cabinet A: {locRepo.GetChildren(conn, cabA.locationId, tx).Count}");

  locRepo.SetActive(conn, cabB.locationId, false, tx);
  Console.WriteLine($"Active:             {locRepo.GetAll(conn, false, tx).Count}, " +
                    $"including inactive: {locRepo.GetAll(conn, true, tx).Count}");

  tx.Rollback();
}

// Copy Repo Test - Does not Insert int the real Database
var copyRepo = new CopyRepository();
using (var conn = db.Connect())
using (var tx = conn.BeginTransaction())
{
  var bolt = printings.First(p => p.Name == "Lightning Bolt");
  var boltId = new PrintingRepository().FindId(conn, bolt, tx)
              ?? throw new InvalidOperationException("Lightning Bolt is not in database.");

  var id = copyRepo.Insert(conn, new Copy
  {
    PrintingId            = boltId,
    Condition             = CardCondition.LP,
    AcquisitionPriceCents = 45000,
    AcquisitionDate       = new DateOnly(2024, 3, 11)
  }, tx);

  var back = copyRepo.GetById(conn, id, tx);
  Console.WriteLine($"Copy {id}: {back?.Condition} {back?.Status}, location {back?.LocationId}, " +
                    $"paid {back?.AcquisitionPriceCents} cents on {back?.AcquisitionDate:yyyy-MM-dd}");

  try
  {
    copyRepo.Insert(conn, new Copy
    {
      PrintingId = boltId,
      Status     = CopyStatus.Sold
    }, tx);
    
    Console.WriteLine("  BAD: incoherent sold row was ACCEPTED");
  }
  catch (SqliteException ex)
  {
    Console.WriteLine($"  Rejected as expected: {ex.Message.Split('\n')[0]}");
  }

  try
  {
    copyRepo.Insert(conn, new Copy { PrintingId = 999999 }, tx);
    Console.WriteLine("  BAD: orphan copy was ACCEPTED");
  }
  catch (SqliteException ex)
  {
    Console.WriteLine($"  Rejected as expected: {ex.Message}");
  }

  Console.WriteLine($"Owned copies inside this transaction: {copyRepo.GetAllOwned(conn, tx).Count}");

  tx.Rollback();
}





/**
foreach (var cardRef in refs)
{
  var printings = await sources[cardRef.Game].GetPrintingAsync(cardRef);

  foreach (var p in printings)
    Console.WriteLine(
      $"{p.Game, -9} {p.SetCode, -7} {p.CollectorNumber, -5} " +
      $"{p.Name, -34} {p.Finish, -12} {p.Rarity, -10} " +
      $"var={p.PrintVariant, -6} tcgp={p.TcgplayerId?.ToString() ?? "-"}"
    );
}
**/