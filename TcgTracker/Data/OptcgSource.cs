using System.Text.Json;

namespace TcgTracker.Data;

public class OptcgSource : ICatalogSource
{
  private readonly HttpClient _http;
  public OptcgSource(HttpClient http) => _http = http;

  public async Task<IEnumerable<Printing>> GetPrintingAsync(CardRef card)
  {
    var cardCode = $"{card.SetCode}-{card.Number}";
    var url = $"https://optcgapi.com/api/sets/card/{cardCode}/";

    var response = await _http.GetAsync(url);
    if (!response.IsSuccessStatusCode)
      return Enumerable.Empty<Printing>();

    var body = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);

    var results = new List<Printing>();

    foreach (var el in doc.RootElement.EnumerateArray())
    {
      var imageId = GetString(el, "card_image_id");
      var (setCode, number) = SplitCardCode(GetString(el, "card_set_id"), card);

      results.Add(new Printing
      {
        Game            =                         "onepiece"               ,
        Name            = CleanName(GetString(el, "card_name"             ), number),
        CardType        =           GetString(el, "card_type",      "none"),
        SetCode         =                          setCode                ,
        CollectorNumber =                          number                ,
        Finish          =                         "normal"                 ,
        Rarity          =           GetString(el, "rarity",         "none"),
        ImageUri        =           GetString(el, "card_image"            ),

        SourceId        =                          imageId                 ,
        SourceName      =                         "optcgapi"               ,
        TcgplayerId     =                          null                    ,
        CardmarketId    =                          null                    ,

        OpParallelType  =       ParseParallel(     imageId                ),
        PrintVariant    =                                           "none" ,
        RawJson         =                          el.GetRawText()         , 
      });
    }
    return results;
  }

  private static string GetString(JsonElement el, string prop, string fallback = "") =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString() ?? fallback
      : fallback;

  private static (string SetCode, string Number) SplitCardCode(string cardSetId, CardRef requested)
  {
    var idx = cardSetId.LastIndexOf('-');
    if (idx > 0 && idx <cardSetId.Length -1) return (cardSetId[..idx], cardSetId[(idx + 1)..]);

    return (requested.SetCode, requested.Number);
  }

  private static string ParseParallel(string imageId)
  {
    var idx = imageId.IndexOf('_');
    return idx >= 0 ? imageId[(idx + 1)..] : "none";
  }

  private static string CleanName(string raw, string collectorNumber)
  {
    var name = raw.Trim();

    if (name.EndsWith("(Parallel)", StringComparison.OrdinalIgnoreCase))
      name = name[..^"(Parallel)".Length].TrimEnd();

    var numberSuffix = $"({collectorNumber})";
    if (name.EndsWith(numberSuffix, StringComparison.Ordinal))
      name = name[..^numberSuffix.Length].TrimEnd();

    return name;
  }
}