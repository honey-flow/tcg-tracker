using System.Text.Json;

namespace TcgTracker.Data;

public class TcgdexSource : ICatalogSource
{
  private readonly HttpClient _http;
  public TcgdexSource(HttpClient http) => _http = http;

  public async Task<IEnumerable<Printing>> GetPrintingAsync(CardRef card)
  {
    var cardCode = $"{card.SetCode}-{card.Number}";
    var url =$"https://api.tcgdex.net/v2/en/cards/{cardCode}"; 

    var response = await _http.GetAsync(url);
    if (!response.IsSuccessStatusCode )
      return Enumerable.Empty<Printing>();

    var body = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;

    var results = new List<Printing>();

    if (!root.TryGetProperty("variants_detailed", out var variants)) 
      return results;  

    foreach (var variant in variants.EnumerateArray())
    {
      var variantType = GetString(variant, "type");
      var (printVariant, promoStamp) = ReadPrintRun(variant);

      results.Add(new Printing
      {
        Game            =                                "pokemon"            ,
        Name            =       GetString(root,          "name"              ),
        CardType        =       GetString(root,          "category",   "none"),
        SetCode         =      GetSetCode(root                               ),
        CollectorNumber =       GetString(root,          "localId"           ),
        Finish          =                                 variantType         ,
        Rarity          =       GetString(root,          "rarity",     "none"),
        ImageUri        =   BuildImageUri(root                               ),

        SourceId        =       GetString(variant,       "variantId"         ),
        PrintVariant    =                                 printVariant        ,
        PkmnPromoStamp  =                                 promoStamp          ,
        SourceName      =                                "tcgdex"             ,
        TcgplayerId     = GetThirdPartyId(variant,       "tcgplayer"         ),
        CardmarketId    = GetThirdPartyId(variant,       "cardmarket"        ),

        RawJson         =                                 body
      });  
    }

    return results;
  }

  private static string GetString(JsonElement el, string prop, string fallback = "") =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString() ?? fallback
      : fallback;

  private static string GetSetCode(JsonElement root) =>
    root.TryGetProperty("set", out var set) && set.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
      ? id.GetString() ?? ""
      : "";

  private static int? GetThirdPartyId(JsonElement variant, string vendor) =>
    variant.TryGetProperty("thirdParty", out var tp) && tp.TryGetProperty(vendor, out var id) && id.ValueKind == JsonValueKind.Number
    ? id.GetInt32()
    : null;

  private static string? BuildImageUri(JsonElement root) =>
    root.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String
      ? $"{img.GetString()}/high.png"
      : null;

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

    return
    (
      run.Count   == 0 ? "none" : string.Join("/", run),
      promo.Count == 0 ? "none" : string.Join("/", promo)
    );
  }
}