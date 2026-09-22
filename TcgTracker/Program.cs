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

var http = new HttpClient();
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