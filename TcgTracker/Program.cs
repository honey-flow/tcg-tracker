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

// Magic the Gathering Bulk Import
const bool runMtgImport = false;

if (runMtgImport)
{
  var mtgBulk = new ScryfallBulk(http, Path.Combine(dataDir, "bulk"));
  var bulkFile = await mtgBulk.EnsureFileAsync(Console.WriteLine);

  var mtgSw = System.Diagnostics.Stopwatch.StartNew();

  var bulkResult = await importer.ImportBulkAsync(
    ScryfallBulk.StreamPrintingsAsync(bulkFile),
    batchSize: 5_000, 
    progress: Console.WriteLine);
  
  mtgSw.Stop();
  
  Console.WriteLine(
    $"MTG: inserted {bulkResult.Inserted:N0}, updated {bulkResult.Updated:N0}, " +
    $"unchanged {bulkResult.Unchanged:N0}, duplicates {bulkResult.Duplicates:N0}, " +
    $"renames {bulkResult.Renames.Count:N0} in {mtgSw.Elapsed.TotalSeconds:N1}s");
}

// Magic the Gathering Price Import
const bool runMtgPrices = false;

if (runMtgPrices)
{
  var priceBulk = new ScryfallBulk(http, Path.Combine(dataDir, "bulk"));
  var priceFile = await priceBulk.EnsureFileAsync(Console.WriteLine);

  var prSw = System.Diagnostics.Stopwatch.StartNew();
  var pr = await new ScryfallPrices(db, new PrintingRepository(), new PriceRepository()).RefreshAsync(priceFile, Console.WriteLine);
  prSw.Stop();

  Console.WriteLine(
    $"MTG Prices (as of {pr.AsOf}): {pr.CardsRead:N0} cards, {pr.PrintingRows:N0} printing rows, " +
    $"{pr.Written.PrintingsCovered:N0} covered, {pr.Written.CurrentRowsWritten} prices written " +
    $"in {prSw.Elapsed.TotalSeconds:N1}s");

  Console.WriteLine(
    $"  not in catalog {pr.NotInCatalog:N0}, ambiguous {pr.Ambiguous:N0}, " +
    $"unknown finish {pr.UnknownFinish:N0}, unparseable {pr.Unparseable:N0}");

  Console.WriteLine(
    $"  history: {pr.Written.SnapshotsAdded:N0} added, {pr.Written.SnapshotsAlreadyStored:N0} already stored");

  if (!pr.Balances) Console.WriteLine("  COUNT MISMATCH: printing rows don't add up");

  //Spot-Check
  using var priceConn = db.Connect();
  var priceRepo = new PriceRepository();
  foreach (var bolt in printings.Where(p => p.Name == "Lightning Bolt"))
  {
    var boltId = new PrintingRepository().FindId(priceConn, bolt);
    if (boltId is null) continue;

    foreach (var o in priceRepo.GetCurrent(priceConn, boltId.Value))
      Console.WriteLine(
        $"  {bolt.SetCode} {bolt.CollectorNumber} {bolt.Finish}: " +
        $"{o.Metric} {o.AmountCents / 100m:N2} {o.Currency} (as of {o.AsOf})");
  }
}

// Pokémon Import
const bool runPokemonImport = false;

if (runPokemonImport) 
{
  var pokemonBulk = new TcgdexBulk(http, db, new CollisionRepository());
  var pkSw = System.Diagnostics.Stopwatch.StartNew();

  var pkResult = await importer.ImportBulkAsync(
    pokemonBulk.StreamPrintingsAsync(Console.WriteLine), batchSize: 5_000, progress: Console.WriteLine);

  pkSw.Stop();

  Console.WriteLine(
    $"Pokémon: inserted {pkResult.Inserted:N0}, updated {pkResult.Updated:N0}, " +
    $"unchanged {pkResult.Unchanged:N0}, duplicates {pkResult.Duplicates:N0}, " +
    $"renames {pkResult.Renames.Count:N0} in {pkSw.Elapsed.TotalSeconds:N1}s");
}

// One Piece Import
const bool runOnePieceImport = true;

if (runOnePieceImport)
{
  var opSw = System.Diagnostics.Stopwatch.StartNew();
  var opBulk = await new OptcgBulk(http).FetchAsync(Console.WriteLine);

  var opResult = importer.Import(opBulk.Printings);

  opSw.Stop();

  Console.WriteLine(
    $"One Piece: fetched {opBulk.RowsFetched:N0}, merged {opBulk.ExactDuplicatesMerged:N0} exact duplicates, " +
    $"held out {opBulk.RowsHeldOut:N0} rows in {opBulk.HeldOut.Count:N0} groups");

  Console.WriteLine(
    $"  inserted {opResult.Inserted:N0}, updated {opResult.Updated:N0}, " +
    $"unchanged {opResult.Unchanged:N0}, duplicates {opResult.Duplicates:N0}, " +
    $"renames {opResult.Renames.Count:N0} in {opSw.Elapsed.TotalSeconds:N1}s");

  if (!opBulk.Balances) Console.WriteLine("  COUNT MISMATCH inside OptcgBulk");
  if (opResult.Total != opBulk.Printings.Count) 
    Console.WriteLine($"  COUNT MISMATCH: handed {opBulk.Printings.Count:N0}, importer accounted for {opResult.Total:N0}");

  foreach (var h in opBulk.HeldOut)
    Console.WriteLine($"  HELD OUT  {h.Reason}: {string.Join(", ", h.ImageIds)}");

  const bool listMergedGroups = true;
  if (listMergedGroups) 
    foreach (var m in opBulk.MergedGroups)
      Console.WriteLine($"  MERGED   {m.ImageId} x{m.Rows}");

  foreach (var r in opResult.Renames)
    Console.WriteLine($"  RENAME    {r.SourceId} -> existing printing {r.ExistingPrintingId}");

  foreach (var c in opBulk.Corrected)
    Console.WriteLine($"  CORRECTED {c.ImageId}: card_set_id replaced with {c.CardSetIdUsed}");
  
  if (opBulk.Balances && opResult.Total == opBulk.Printings.Count)
  {
    var runAt     = CatalogIssueRepository.NewRunStamp();
    var issueRepo = new CatalogIssueRepository();

    using var issueConn = db.Connect();
    var stored = new PrintingRepository().LoadAllKeys(issueConn);
    var issues = OptcgIssues.Build(opBulk, stored);
    var rec    = issueRepo.RecordRun(issueConn, OptcgIssues.Game, OptcgIssues.Source, issues, runAt);

    var open = issueRepo.GetOpen(issueConn, OptcgIssues.Game);
    Console.WriteLine($"  Issues: {rec.Seen:N0} seen this run, {rec.Resolved:N0} resolved, {open.Count:N0} open");
    foreach (var g in open.GroupBy(i => i.Issue.Kind))
      Console.WriteLine($"    {g.Key, -10} {g.Count():N0}");
  }
  else
  {
    Console.WriteLine("  Issues NOT recorded: counts did not balance, so this run can't be trusted to resolve anything.");
  }
}

const bool runReconcile = true;
if (runReconcile)
{
  var collisionRepo = new CollisionRepository();
  var reconciler = new VariantReconciler(http, db, new PrintingRepository(), collisionRepo);

  var rcSW = System.Diagnostics.Stopwatch.StartNew();
  var rcResult = await reconciler.RunAsync(delayMs: 150, progress: Console.WriteLine);
  rcSW.Stop();

  Console.WriteLine(
    $"Reconcile: {rcResult.CardProcessed:N0} cards, " +
    $"{rcResult.PrintingsInserted:N0} printings added, " +
    $"{rcResult.Failed:N0} failed in {rcSW.Elapsed.TotalSeconds:N1}s");
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

const bool RunPriceHistoryTest = true;

if (RunPriceHistoryTest)
{
  var histFile  = await new ScryfallBulk(http, Path.Combine(dataDir, "bulk")).EnsureFileAsync();
  var refresher = new ScryfallPrices(db, new PrintingRepository(), new PriceRepository());

  using var testConn = db.Connect();
  var testBolt   = printings.First(p => p.Name == "Lightning Bolt");
  var testBoltId = new PrintingRepository().FindId(testConn, testBolt) 
                   ?? throw new InvalidOperationException("Lightning Bolt is not in database.");

  if (new PriceRepository().LoadHeldPrintingIds(testConn, "mtg").Contains(testBoltId))
  {
    Console.WriteLine("History test SKIPPED: this Bolt is really held; cleanup would delete its history.");
  }
  else
  {
    var testCopyId = new CopyRepository().Insert(testConn, new Copy { PrintingId = testBoltId, Condition = CardCondition.NM });

    try
    {
      var run1 = await refresher.RefreshAsync(histFile);
      var run2 = await refresher.RefreshAsync(histFile);

      using var count = testConn.CreateCommand();
      count.CommandText = "SELECT COUNT(*) FROM current_prices WHERE source = 'scryfall';";
      var storedRows = Convert.ToInt64(count.ExecuteScalar());

      using var snap = testConn.CreateCommand();
      snap.CommandText = @"
                           SELECT json_extract(payload, '$.usd') FROM price_snapshots
                           WHERE printing_id = $printing_id AND source = 'scryfall';";
      snap.Parameters.AddWithValue("$printing_id", testBoltId);
      var snapshotUsd = snap.ExecuteScalar();

      Check("run 1 history", run1.Written.SnapshotsAdded == 1 && run1.Written.SnapshotsAlreadyStored == 0,
            $"{run1.Written.SnapshotsAdded} added, {run1.Written.SnapshotsAlreadyStored} already stored");
      Check("run 2 history", run2.Written.SnapshotsAdded == 0 && run2.Written.SnapshotsAlreadyStored == 1,
            $"{run2.Written.SnapshotsAdded} added, {run2.Written.SnapshotsAlreadyStored} already stored");
      Check("current replaced, not appended", storedRows == run2.Written.CurrentRowsWritten,
            $"{storedRows:N0} stored, {run2.Written.CurrentRowsWritten:N0} written");
      Console.WriteLine($"  snapshot usd: {snapshotUsd} (should match the Bolt's usd above)");
    }
    finally
    {
      using var cleanup = testConn.CreateCommand();
      cleanup.CommandText = @"
                              DELETE FROM price_snapshots WHERE printing_id = $printing_id AND source = 'scryfall';
                              DELETE FROM copies          WHERE copy_id     = $copy_id;";
      cleanup.Parameters.AddWithValue("$printing_id", testBoltId);
      cleanup.Parameters.AddWithValue("$copy_id",     testCopyId);
      cleanup.ExecuteNonQuery();
    }
  }

  static void Check(string what, bool ok, string detail) => Console.WriteLine($"  {(ok ? "PASS" : "FAIL")} {what}: {detail}");
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