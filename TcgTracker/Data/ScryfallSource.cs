using System.Text.Json;

namespace TcgTracker.Data;

public class ScryfallSource : ICatalogSource
{
  private readonly HttpClient _http;
  public ScryfallSource(HttpClient http) => _http = http;

  public async Task<IEnumerable<Printing>> GetPrintingAsync(CardRef card)
  {
    var url = $"https://api.scryfall.com/cards/{card.SetCode.ToLowerInvariant()}/{card.Number}";

    var response = await _http.GetAsync(url);
    if (!response.IsSuccessStatusCode)
        return Enumerable.Empty<Printing>();

    var body = await response.Content.ReadAsStringAsync();

    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;

    var results = new List<Printing>();

    foreach (var finishName in ReadFinishes(root))
    {

      results.Add(new Printing
      {
        Game            =                           "mtg"                      ,
        Name            =     GetString(root,       "name"                    ),
        CardType        =     GetString(root,       "type_line",        "none"), 
        SetCode         =     GetString(root,       "set"                     ),
        CollectorNumber =     GetString(root,       "collector_number"        ),
        Finish          =                            finishName                ,
        Rarity          =     GetString(root,       "rarity",           "none"),
        ImageUri        =   GetImageUri(root)                                  ,

        SourceId        =     GetString(root,       "id"                      ),
        SourceName      =                           "scryfall"                 ,
        TcgplayerId     =        GetInt(root,       "tcgplayer_id"            ),
        CardmarketId    =        GetInt(root,       "cardmarket_id"           ),

        MtgFrameStyle   = GetFrameStyle(root                                  ),

        RawJson         =                            body
      });
    }

    return results;
  }

  private static string GetString(JsonElement el, string prop, string fallback = "") => 
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString() ?? fallback
      : fallback;
    
  private static int? GetInt(JsonElement el, string prop) =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
      ? v.GetInt32() 
      : null;
  
  private static string? GetImageUri(JsonElement root)
  {
    var top = NormalImage(root);
    if (top is not null) return top;

    if (root.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array)
      foreach (var face in faces.EnumerateArray())
      {
        var uri = NormalImage(face);
        if (uri is not null) return uri;
      } 
    return null;  
  }

  private static string? NormalImage(JsonElement el) =>
    el.TryGetProperty("image_uris", out var uris) && uris.TryGetProperty("normal", out var n) && n.ValueKind == JsonValueKind.String
      ? n.GetString()
      : null;
      
  private static List<string> ReadFinishes(JsonElement root)
  {
    var finishes = new List<string>();

    if (root.TryGetProperty("finishes", out var arr) && arr.ValueKind == JsonValueKind.Array)
    {
      foreach (var f in arr.EnumerateArray())
        if (f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } name) finishes.Add(name);

      return finishes;
    }

    if (IsTrue(root, "nonfoil")) finishes.Add("nonfoil");
    if (IsTrue(root, "foil"))    finishes.Add("foil");
    return finishes;
  }

  private static bool IsTrue(JsonElement el, string prop) =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

  private static string GetFrameStyle(JsonElement root)
  {
    if (!root.TryGetProperty("frame_effects", out var effects)) return "none";

    var first = effects.EnumerateArray().FirstOrDefault();
    return first.ValueKind == JsonValueKind.String
      ? first.GetString() ?? "none"
      : "none";
  }
}