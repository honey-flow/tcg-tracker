using System.Text.Json;
using System.Text.RegularExpressions;

namespace TcgTracker.Data;

public class OptcgSource : ICatalogSource
{
  private readonly HttpClient _http;
  public OptcgSource(HttpClient http) => _http = http;

  public async Task<IEnumerable<Printing>> GetPrintingAsync(CardRef card)
  {
    var cardCode = $"{card.SetCode}-{card.Number}";
    var url = $"https://optcgapi.com/api/sets/card/{cardCode}/";

    using var response = await _http.GetAsync(url);
    
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
      return Enumerable.Empty<Printing>();

    if (!response.IsSuccessStatusCode)
      throw new HttpRequestException($"optcgapi returned {(int)response.StatusCode} for {cardCode} ({url})");

    var body = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);

    return doc.RootElement.EnumerateArray().Select(Map).ToList();
  }
  
  public static Printing Map(JsonElement el)
  {
    var cardSetId = GetString(el, "card_set_id");
    var imageId   = GetString(el, "card_image_id");

    if (Corrections.TryGetValue(imageId, out var corrected))
      cardSetId = corrected;

    var cardBase = CardNumberOf(cardSetId);
    var (setCode, number) =SplitCardCode(cardBase, imageId);

    var (prefix, suffix) = ParseImageId(imageId, cardBase);

    return new Printing
    {
      Game            =                         "onepiece"               ,
      Name            = CleanName(GetString(el, "card_name"             ), imageId),
      CardType        =           GetString(el, "card_type",      "none"),
      SetCode         =                     setCode                 ,
      CollectorNumber =                     number                  ,
      Finish          =                         "normal"                 ,
      Rarity          =           GetString(el, "rarity",         "none"),
      ImageUri        =   GetNullableString(el, "card_image"            ),

      SourceId        =                     imageId.Length > 0 ? imageId : null,
      SourceName      =                         "optcgapi"               ,
      TcgplayerId     =                          null                    ,
      CardmarketId    =                          null                    ,

      VariantKey      =                          VariantKeyOf(prefix, suffix),

      OpParallelType  =                          suffix.StartsWith("p", StringComparison.OrdinalIgnoreCase) ? "parallel" : "none",
      PrintVariant    =                          suffix.StartsWith("r", StringComparison.OrdinalIgnoreCase) ? "reprint"  : "none",

      RawJson         =                          el.GetRawText()         , 
    };
  }

  public static readonly IReadOnlyDictionary<string, string> Corrections = new Dictionary<string, string>(StringComparer.Ordinal)
  {
    ["OP09-051_p2"] = "OP09-051"
  };

  private static readonly Regex DonId = new(@"^don_([0-9]+)$");

  public static Printing MapDon(JsonElement el)
  {
    var imageId = GetString(el, "card_image_id");

    var m = DonId.Match(imageId);
    if (!m.Success) throw new FormatException($"DON card_image_id '{imageId}' is not shaped like don_<number>");

    var name = GetString(el, "card_name").Trim();
    if (name.Length == 0) throw new FormatException($"DON card_name is empty ({imageId})");

    return new Printing
    {
      Game            = "onepiece",
      Name            = name,
      CardType        = GetString(el, "card_type", "none"),
      SetCode         = "DON",
      CollectorNumber = m.Groups[1].Value,
      Finish          = "normal",
      Rarity          = GetString(el, "rarity", "none"),
      ImageUri        = GetNullableString(el, "card_image"),
      SourceId        = imageId,
      SourceName      = "optcgapi",
      VariantKey      = "none",
      OpParallelType  = "none",
      PrintVariant    = "none",
      RawJson         = el.GetRawText(),
    };
  }
  
  private static readonly HashSet<String> ParenLabels = new(StringComparer.OrdinalIgnoreCase)
  {
    "Alternate Art", "Parallel", "SP", "SPR", "TR", "Manga", "Reprint",
    "Full Art", "Box Topper", "Pirate Foil", "Jolly Roger Foil", "Textured Foil",
    "Wanted Poster","Dash Pack", "Pandaman Art", 
    "Super Alternate Art", "Red Super Alternate Art", "Super Leader Alternate Art",
    "Gold", "Silver", "Gold-Stamped Signature",
  };

  private static readonly HashSet<String> DashLabels = new(StringComparer.OrdinalIgnoreCase)
  {
    "Two Legends",
  };

  private static readonly Regex CardCode = new(@"^[A-Z]+[0-9]*-[0-9]+$");
  private static readonly Regex SetCode  = new(@"^[A-Z]+[0-9]+$");

  public static string CleanName(string raw, string imageId)
  {
    var name = raw.Trim();
    while (true)
    {
      if (name.EndsWith(')'))
      {
        var open = name.LastIndexOf('(');
        if (open > 0 && IsParenLabel(name[(open + 1)..^1]))
        {
          name = name[..open].TrimEnd();
          continue;
        }
      }

      var dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
      if (dash > 0 && IsDashLabel(name[(dash + 3)..]))
      {
        name = name[..dash].TrimEnd();
        continue;
      }

      break;
    }

    if (name.Length == 0) throw new FormatException($"card_name '{raw}' is empty after cleaning ({imageId})");

    return name;
  }

  private static bool IsParenLabel(string s) =>
    s.Length > 0
    && (s.All(char.IsAsciiDigit)
        || CardCode.IsMatch(s)
        || SetCode.IsMatch(s)
        || ParenLabels.Contains(s));

  private static bool IsDashLabel(string s) => CardCode.IsMatch(s) || DashLabels.Contains(s);

  private static string CardNumberOf(string cardSetId)
  {
    var underscore = cardSetId.IndexOf('_');
    var id = underscore >= 0 ? cardSetId[..underscore] : cardSetId;

    var dash = id.LastIndexOf('-');
    if (dash > 0 && IsVariantTag(id[(dash + 1)..]))
      return id[..dash];

    return id;
  }

  private static bool IsVariantTag(string s) =>
    s.Length >= 2 && (s[0] is 'p' or 'P' or 'r' or 'R')
                  && s[1..].All(char.IsAsciiDigit);

  private static (string Prefix, string Suffix) ParseImageId(string imageId, string cardBase)
  {
    if (imageId.Length == 0) return ("", "");

    var start = 0;
    while (true)
    {
      var idx = imageId.IndexOf(cardBase, start, StringComparison.Ordinal);
      if (idx < 0)
        throw new FormatException($"card_image_id '{imageId}' does not contain '{cardBase}' on a boundary");

      var end = idx + cardBase.Length;
      var beforeOk = idx == 0 || imageId[idx -1] == '_';
      var afterOk = end == imageId.Length || imageId[end] == '_' || imageId[end] == '-';

      if (beforeOk && afterOk)
        return (imageId[..idx].TrimEnd('_'), imageId[end..].TrimStart('_', '-'));

      start = idx + 1;
    }
  }

  private static string VariantKeyOf(string prefix, string suffix) => 
    (prefix.Length, suffix.Length) switch
    {
      (0, 0) => "none",
      (0, _) => suffix,
      (_, 0) => prefix, 
      _      => $"{prefix}_{suffix}"
    };

  private static (string SetCode, string Number) SplitCardCode(string cardBase, string imageId)
  {
    var idx = cardBase.LastIndexOf('-');
    if (idx > 0 && idx <cardBase.Length -1) return (cardBase[..idx], cardBase[(idx + 1)..]);

    throw new FormatException($"card_set_id '{cardBase}' has no set/number separator ({imageId})");
  }

  private static string GetString(JsonElement el, string prop, string fallback = "") =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString() ?? fallback
      : fallback;

  private static string? GetNullableString(JsonElement el, string prop) =>
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
      ? v.GetString()
      : null;
}