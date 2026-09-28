using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TcgTracker.Data;

public record ReconcileResult(int CardProcessed, int PrintingsInserted, int Failed);

public class VariantReconciler{
  private readonly HttpClient _http;
  private readonly Database _db;
  private readonly PrintingRepository _printings;
  private readonly CollisionRepository _collisions;

  public VariantReconciler(HttpClient http, Database db, PrintingRepository printings, CollisionRepository collisions)
  {
    _http = http;
    _db = db;
    _printings = printings;
    _collisions = collisions;
  }
  
  public async Task<ReconcileResult> RunAsync(int delayMs = 150, Action<string>? progress = null)
  {
    using var conn = _db.Connect();

    var pending = _collisions.GetPending(conn, "tcgdex");
    progress?.Invoke($"  {pending.Count:N0} cards to reconcile.");

    var processed = 0;
    var inserted  = 0;
    var failed    = 0;

    foreach (var work in pending)
    {
      JsonDocument? doc = null;

      try
      {
        doc = await FetchCardAsync(work.SourceCardId);
      }
      catch (HttpRequestException ex)
      {
        progress?.Invoke($"  FAILED {work.SourceCardId}: {ex.Message}");
        failed++;
        continue;
      }

      using (doc)
      {
        var added = InsertMissingVariants(conn, doc.RootElement, work.SourceCardId);
        inserted += added;

        _collisions.MarkResolved(conn, work.SourceName, work.SourceCardId);
      }

      processed++;

      if (processed % 50 == 0)
        progress?.Invoke($"  {processed:N0}/{pending.Count:N0} reconciled, {inserted:N0} printings added.");

      if (delayMs > 0) await Task.Delay(delayMs);
    }

    return new ReconcileResult(processed, inserted, failed);
  }

  private async Task<JsonDocument> FetchCardAsync(string cardId)
  {
    var url = $"https://api.tcgdex.net/v2/en/cards/{cardId}";

    var response = await _http.GetAsync(url);
    response.EnsureSuccessStatusCode();

    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  }

  private int InsertMissingVariants(SqliteConnection conn, JsonElement root, string cardId)
  {
    if (!root.TryGetProperty("variants_detailed", out var variants) || variants.ValueKind != JsonValueKind.Array)
      return 0;

    var setCode = SplitCardId(cardId).SetCode;
    var added = 0;

    using var tx = conn.BeginTransaction();
    
    foreach (var variant in variants.EnumerateArray())
    {
      var variantId = GetString(variant, "variantId");
      if (variantId.Length == 0) continue;

      var variantType = GetString(variant, "type");
      var (printVariant, promoStamp) = ReadPrintRun(variant);

      var printing = new Printing
      {
        Game            =                "pokemon",
        Name            =       GetString(root,        "name"),
        CardType        =       GetString(root,        "category",   "none"),
        SetCode         =      GetSetCode(root,        setCode),
        CollectorNumber =       GetString(root,        "localId"),
        Finish          =                 variantType,
        Rarity          =       GetString(root,        "rarity",     "none"),
        ImageUri        =   BuildImageUri(root),

        SourceId        =                 variantId,
        SourceName      =                "tcgdex",
        TcgplayerId     = GetThirdPartyId(variant,     "tcgplayer"),
        CardmarketId    = GetThirdPartyId(variant,     "cardmarket"),

        PrintVariant    =                 printVariant,
        PkmnPromoStamp  =                 promoStamp,

        VariantKey      =                 variantId,
        
        RawJson         =                 null
      };
      
      if (_printings.FindId(conn, printing, tx) is not null) continue;

      _printings.Insert(conn, printing, tx);
      added++;
    }

    tx.Commit();
    return added;
  }

  private static (string SetCode, string Number) SplitCardId(string id)
  {
    var idx = id.LastIndexOf('-');
    return idx > 0 && idx < id.Length - 1 ? (id[..idx], id[(idx + 1)..]) : (id, "");
  }

  private static string GetSetCode(JsonElement root, string fallback) =>
    root.TryGetProperty("set", out var set) && set.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
      ? id.GetString() ?? fallback
      : fallback;

  private static (string PrintVariant, string PromoStamp) ReadPrintRun(JsonElement variant)
  {
    var run = new List<string>();
    var promo = new List<string>();

    if (variant.TryGetProperty("stamp", out var stamps) && stamps.ValueKind == JsonValueKind.Array)
      foreach (var s in stamps.EnumerateArray())
      {
        var value = s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrEmpty(value)) continue;
        if (value == "1st-edition") run.Add(value); else promo.Add(value);
      }

    var subtype = GetString(variant, "subtype");
    if (subtype.Length > 0) run.Add(subtype);

    promo.Sort(StringComparer.Ordinal);

    return (
      run.Count   == 0 ? "none" : string.Join("/", run),
      promo.Count == 0 ? "none" : string.Join("/", promo));
  }

  private static int? GetThirdPartyId(JsonElement variant, string vendor) =>
    variant.TryGetProperty("thirdParty", out var tp) && tp.TryGetProperty(vendor, out var id) && id.ValueKind == JsonValueKind.Number
      ? id.GetInt32()
      : null;

  private static string? BuildImageUri(JsonElement root) => root.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String
    ? $"{img.GetString()}/high.png"
    : null;

  private static string GetString(JsonElement el, string prop, string fallback = "") => 
    el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString() ?? fallback
      : fallback; 
}
