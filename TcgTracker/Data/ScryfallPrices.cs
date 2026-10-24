using System.Text.Json;

namespace TcgTracker.Data;

public record ScryfallPriceResult(
  string           AsOf,
  int              CardsRead,
  int              PrintingRows,
  int              NotInCatalog,
  int              Ambiguous,
  int              UnknownFinish,
  int              Unparseable,
  PriceWriteResult Written)
{
  public bool Balances => PrintingRows == Written.PrintingsCovered + NotInCatalog + Ambiguous;
}

public class ScryfallPrices
{
  public const string Source = "scryfall";

  private static readonly IReadOnlyDictionary<string, (string Usd, string Eur)> MetricsByFinish = new Dictionary<string, (string Usd, string Eur)>
  {
    ["nonfoil"] = ("usd",        "eur"),
    ["foil"]    = ("usd_foil",   "eur_foil"),
    ["etched"]  = ("usd_etched", "eur_etched")
  };

  private readonly Database           _db;
  private readonly PrintingRepository _printings;
  private readonly PriceRepository    _prices;

  public ScryfallPrices(Database db, PrintingRepository printings, PriceRepository prices)
  {
    _db        = db;
    _printings = printings;
    _prices    = prices;
  }

  public async Task<ScryfallPriceResult> RefreshAsync(string bulkFile, Action<string>? progress = null)
  {
    var asOf      = ScryfallBulk.VersionOf(bulkFile);
    var fetchedAt = CatalogIssueRepository.NewRunStamp();

    using var conn = _db.Connect();

    progress?.Invoke("  reading printings and held copies...");
    var bySourceId = _printings.LoadSourceIds(conn);
    var held       = _prices.LoadHeldPrintingIds(conn, "mtg");
    progress?.Invoke($"  {held.Count:N0} Magic printings held (history kept for these)");

    var covered   = new List<long>();
    var current   = new List<PriceObservation>();
    var snapshots = new List<PriceSnapshot>();

    int cards = 0, rows = 0, notInCatalog = 0, ambiguous = 0, unknownFinish = 0, unparseable = 0;

    await foreach (var line in ScryfallBulk.ReadLinesAsync(bulkFile))
    {
      using var doc = JsonDocument.Parse(line);
      var root = doc.RootElement;
      cards++;

      JsonElement? prices = root.TryGetProperty("prices", out var block) && block.ValueKind ==JsonValueKind.Object
        ? block
        : null;

      foreach (var p in ScryfallSource.MapCard(root, rawJson: null))
      {
        rows++;

        if (p.SourceId is not { Length: > 0 } sourceId || !bySourceId.TryGetValue((Source, sourceId, p.Finish), out var ids))
        {
          notInCatalog++;
          continue;
        }

        if (ids.Count != 1)
        {
          ambiguous++;
          continue;
        }

        var printingId = ids[0];

        covered.Add(printingId);

        if (prices is JsonElement forHistory && held.Contains(printingId))
          snapshots.Add(new PriceSnapshot(printingId, Source, asOf, forHistory.GetRawText()));

        if (!MetricsByFinish.TryGetValue(p.Finish, out var metrics))
        {
          unknownFinish++;
          continue;
        }

        if (prices is not JsonElement forCurrent) continue;

        unparseable += AddPrice(current, forCurrent, printingId, metrics.Usd, "USD", asOf);
        unparseable += AddPrice(current, forCurrent, printingId, metrics.Eur, "EUR", asOf);
      }

      if (cards % 20_000 == 0) progress?.Invoke($"  {cards:N0} cards read...");
    }

    progress?.Invoke($"  writing {current.Count:N0} prices for {covered.Count:N0} printings...");
    var written = _prices.Write(conn, Source, covered, current, snapshots, fetchedAt);

    return new ScryfallPriceResult(asOf, cards, rows, notInCatalog, ambiguous, unknownFinish, unparseable, written);
  }

  private static int AddPrice(List<PriceObservation> into, JsonElement prices, long printingId, string metric, string currency, string asOf)
  {
    if (!prices.TryGetProperty(metric, out var value) || value.ValueKind == JsonValueKind.Null)
      return 0;

    if (value.ValueKind == JsonValueKind.String && Money.TryParseCents(value.GetString() ?? "", out var cents))
    {
      into.Add(new PriceObservation(printingId, Source, metric, currency, cents, asOf));
      return 0;
    }

    return 1;
  }
}

