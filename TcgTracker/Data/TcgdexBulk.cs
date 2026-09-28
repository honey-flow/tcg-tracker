using System.Net.Http.Json;
using System.Text.Json;

namespace TcgTracker.Data;

public class TcgdexBulk
{
  private const string GraphQlUrl = "https://api.tcgdex.net/v2/graphql";

  private const int PageSize = 150;

  private readonly HttpClient _http;
  private readonly Database _db;
  private readonly CollisionRepository _collisions;

  public TcgdexBulk(HttpClient http, Database db, CollisionRepository collisions)
  {
    _http = http;
    _db = db;
    _collisions = collisions;
  }

  public async IAsyncEnumerable<Printing> StreamPrintingsAsync(Action<string>? progress = null)
  {
    var pocketSets = await LoadPocketSetIdsAsync();
    progress?.Invoke($"  excluding {pocketSets.Count} Pokémon TCG Pocket sets");

    var page = 1;
    var skipped = 0;
    var collisions = 0;

    while (true)
    {
      var cards = await FetchPageAsync(page);

      progress?.Invoke($"  page {page}: {cards.Count} cards");

      foreach (var card in cards)
      {
        var id = GetString(card, "id");
        var (setCode, _) = SplitCardId(id);

        if (pocketSets.Contains(setCode)) {skipped++; continue;}

        var seenInCard = new HashSet<string>(StringComparer.Ordinal);
        var collided = 0;

        foreach (var printing in MapCard(card, setCode)) 
        {
          var variantKey = $"{printing.Finish}|{printing.PrintVariant}|{printing.PkmnPromoStamp}";

          if (!seenInCard.Add(variantKey)) {collided++; continue;}
          
          yield return printing;
        }

        if (collided > 0)
        {
          collisions++;
          RecordCollision(id, collided + 1);
        }
      }

      if (cards.Count < PageSize) break;
      page++;
    }

    progress?.Invoke($"  skipped {skipped} Pocket cards, " +
                     $"{collisions} cards had indistinguishable variants");
  }

  private void RecordCollision(string cardId, int count)
  {
    using var conn = _db.Connect();
    _collisions.Record(conn, new VariantCollision("pokemon", "tcgdex", cardId, count));
  }

  private async Task<HashSet<string>> LoadPocketSetIdsAsync()
  {
    var doc = await QueryAsync("{ sets { id serie { id } } }");

    var pocket = new HashSet<string>(StringComparer.Ordinal);

    foreach (var set in doc.RootElement.GetProperty("data").GetProperty("sets").EnumerateArray())
      if (set.TryGetProperty("serie", out var serie) && GetString(serie, "id") == "tcgp")
        pocket.Add(GetString(set, "id"));

    return pocket;
  }  

  private async Task<List<JsonElement>> FetchPageAsync(int page)
  {
    var query = $@"
      {{ 
        cards(filters: {{}}, pagination: {{ page: {page}, itemsPerPage: {PageSize} }}) 
        {{
          id 
          localId 
          name
          category
          rarity
          image
          variants_detailed {{ type subtype stamp }}
        }}
      }}";

    var doc = await QueryAsync(query);

    return doc.RootElement.GetProperty("data").GetProperty("cards").EnumerateArray().Select(e => e.Clone()).ToList();
  }

  private async Task<JsonDocument> QueryAsync(string query)
  {
    using var response = await _http.PostAsJsonAsync(GraphQlUrl, new { query });
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(body);

    if (doc.RootElement.TryGetProperty("errors", out var errors))
    {
      var first = errors.EnumerateArray().FirstOrDefault();
      doc.Dispose();
      throw new InvalidOperationException($"TCGDex GraphQL error: {GetString(first, "message")}");
    }

    return doc;
  }

  private static IEnumerable<Printing> MapCard(JsonElement card, string setCode)
  {
    if (!card.TryGetProperty("variants_detailed", out var variants) || variants.ValueKind != JsonValueKind.Array)
      yield break;

    foreach (var variant in variants.EnumerateArray())
    {
      var variantType = GetString(variant, "type");
      var (printVariant, promoStamp) = ReadPrintRun(variant);

      yield return new Printing
      {
        Game            =              "pokemon"                 ,
        Name            =     GetString(card, "name"            ),
        CardType        =     GetString(card, "category", "none"),
        SetCode         =               setCode                  ,
        CollectorNumber =     GetString(card, "localId"         ),
        Finish          =               variantType              ,
        Rarity          =     GetString(card, "rarity"  , "none"),
        ImageUri        = BuildImageUri(card)                    ,

        SourceId        =               null                     ,
        SourceName      =              "tcgdex"                  ,
        TcgplayerId     =               null                     ,
        CardmarketId    =               null                     ,

        PrintVariant    =               printVariant             ,
        PkmnPromoStamp  =               promoStamp               ,

        RawJson         =               null                      
      };
    }
  }

  private static (string SetCode, string Number) SplitCardId(string id)
  {
    var idx = id.LastIndexOf('-');
    return idx > 0 && idx < id.Length - 1
      ? (id[..idx], id [(idx + 1)..])
      : (id, "");
  }

  private static (string PrintVariant, string PromoStamp) ReadPrintRun(JsonElement variant)
  {
    var run = new List<string>();
    var promo = new List<string>();

    if (variant.TryGetProperty("stamp", out var stamps) && stamps.ValueKind == JsonValueKind.Array)
    {
      foreach (var s in stamps.EnumerateArray())
      {
        var value = s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        if (string.IsNullOrEmpty(value)) continue;
        if (value == "1st-edition") run.Add(value); else promo.Add(value);
      }
    }

    var subtype = GetString(variant, "subtype");
    if (subtype.Length > 0) run.Add(subtype);

    promo.Sort(StringComparer.Ordinal);

    return (
      run.Count   == 0 ? "none" : string.Join("/", run),
      promo.Count == 0 ? "none" : string.Join("/", promo));
  }

  private static string? BuildImageUri(JsonElement card) => card.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String
    ? $"{image.GetString()}/high.png"
    : null;

  private static string GetString(JsonElement el, string prop, string fallback = "") => 
    el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString() ?? fallback
      : fallback;
}
