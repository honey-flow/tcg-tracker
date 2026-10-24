using System.Text.Json;

namespace TcgTracker.Data;

public record HeldOut(string IssueKey, string Reason, IReadOnlyList<string> ImageIds);

public record MergedGroup(string IssueKey, string ImageId, int Rows);

public record CorrectedRow(string ImageId, string CardSetIdUsed);

public record OptcgBulkResult(IReadOnlyList<Printing> Printings, int RowsFetched, int ExactDuplicatesMerged, IReadOnlyList<MergedGroup> MergedGroups, IReadOnlyList<HeldOut> HeldOut, IReadOnlyList<CorrectedRow> Corrected)
{
  public int RowsHeldOut => HeldOut.Sum(h => h.ImageIds.Count);
  public bool Balances => Printings.Count + ExactDuplicatesMerged + RowsHeldOut == RowsFetched;
}

public class OptcgBulk
{
  private enum Origin { Set, Starter, Promo, Don }

  private static readonly (string Url, Origin From)[] Endpoints =
  {
    ("https://optcgapi.com/api/allSetCards/", Origin.Set),
    ("https://optcgapi.com/api/allSTCards/", Origin.Starter),
    ("https://optcgapi.com/api/allPromos/", Origin.Promo),
    ("https://optcgapi.com/api/allDonCards/", Origin.Don)
  };

  private sealed record Row(Printing P, Origin From, string RawName);

  private readonly HttpClient _http;
  public OptcgBulk(HttpClient http) => _http = http;

  private sealed class Buckets
  {
    public List<Printing>    Printings    {get;} = new();
    public List<MergedGroup> MergedGroups {get;} = new();
    public List<HeldOut>     HeldOut      {get;} = new();
    public List<Row>         Rekeyed      {get;} = new();
    public int Merged;
  }

  public async Task<OptcgBulkResult> FetchAsync(Action<string>? progress = null)
  {
    var rows      = new List<Row>();
    var b         = new Buckets();
    var corrected = new List<CorrectedRow>();
    var fetched   = 0;

    foreach (var (url, origin) in Endpoints)
    {
      using var doc = await FetchArrayAsync(url, progress);
      var count = 0;

      foreach (var el in doc.RootElement.EnumerateArray())
      {
        count++;
        var imageId = ReadString(el, "card_image_id", "(no card_image_id)");
        var rawName = ReadString(el, "card_name", "(no card_name)");

        Printing p;
        try
        {
          p = origin == Origin.Don ? OptcgSource.MapDon(el) : OptcgSource.Map(el);
        }
        catch (FormatException ex)
        {
          b.HeldOut.Add(new HeldOut("unmapped:" + imageId,$"{ex.Message} [name '{rawName}']", new[] { imageId }));
          continue;
        }

        if (OptcgSource.Corrections.TryGetValue(imageId, out var used))
          corrected.Add(new CorrectedRow(imageId, used));

        rows.Add(new Row(p, origin, rawName.Trim()));
      }

      fetched += count;
      progress?.Invoke($"  {url}: {count:N0} rows");
    }

    foreach (var group in GroupByKey(rows))
      ResolveNatural(group, b);

    foreach (var group in GroupByKey(b.Rekeyed))
      ResolveRekeyed(group, b);

    return new OptcgBulkResult(b.Printings, fetched, b.Merged, b.MergedGroups, b.HeldOut, corrected);
  }

  private static List<List<Row>> GroupByKey(IEnumerable<Row> rows)
  {
    var groups = new Dictionary<PrintingKey, List<Row>>();
    var order  = new List<PrintingKey>();
    foreach (var r in rows)
    {
      var key = PrintingRepository.KeyOf(r.P);
      if (!groups.TryGetValue(key, out var list))
      {
        list = new List<Row>();
        groups[key] = list;
        order.Add(key);
      }
      list.Add(r);
    }
    
    return order.Select(k => groups[k]).ToList();
  }
  
  private static void ResolveNatural(List<Row> g, Buckets b)
  {
    if (g.Count == 1) { b.Printings.Add(g[0].P); return; }

    if (g.All(r => SameStoredIdentity(r.P, g[0].P))) { MergeInto(g, b); return; }

    var baseRows  = g.Where(r => r.From != Origin.Promo).ToList();
    var promoRows = g.Where(r => r.From == Origin.Promo).ToList();

    List<Row>? keptFrom = null;

    if (baseRows.Count > 0)
    {
      if (baseRows.All(r => SameStoredIdentity(r.P, baseRows[0].P)))
      {
        keptFrom = baseRows;
      }
      else if (IsRarityTrSplit(baseRows, out var nonTr, out var tr))
      {
        keptFrom = nonTr;
        foreach (var t in tr)
          b.Rekeyed.Add(Rekey(t, printVariant: t.P.PrintVariant, variantKey: "rarity:TR"));
      }
      else
      {
        var k = PrintingRepository.KeyOf(baseRows[0].P);
        b.HeldOut.Add(new HeldOut(CatalogIssueRepository.IssueKeyOf(k),
          $"{baseRows.Count} set/starter rows share key {k.SetCode}-{k.CollectorNumber} [{k.VariantKey}] " +
          $"but differ in {Differences(baseRows.Select(r => r.P).ToList())}",
          baseRows.Select(r => r.P.SourceId ?? "(no image id)").ToList()));
      }
    }

    var kept = keptFrom is null ? null : Survivor(keptFrom);
    if (kept is not null) b.Printings.Add(kept);
    var merges = keptFrom is null ? 0 : keptFrom.Count - 1;

    foreach (var pr in promoRows)
    {
      if (kept is not null && SameStoredIdentity(pr.P, kept))
      {
        merges++;
        continue;
      }

      b.Rekeyed.Add(Rekey(pr, printVariant: "promo", variantKey: "promo:" + pr.RawName));
    }

    if (merges > 0)
    {
      b.Merged += merges;
      b.MergedGroups.Add(new MergedGroup(CatalogIssueRepository.IssueKeyOf(PrintingRepository.KeyOf(kept!)), kept!.SourceId ?? "(no image id)", merges + 1));
    }
  }

  private static void ResolveRekeyed(List<Row> g, Buckets b)
  {
    if (g.Count == 1) { b.Printings.Add(g[0].P); return; }
    if (g.All(r => SameStoredIdentity(r.P, g[0].P))) { MergeInto(g, b); return; }

    var k = PrintingRepository.KeyOf(g[0].P);
    b.HeldOut.Add(new HeldOut(CatalogIssueRepository.IssueKeyOf(k),
      $"{g.Count} rows share re-keyed key {k.SetCode}-{k.CollectorNumber} [{k.VariantKey}] " +
      $"but differ in {Differences(g.Select(r => r.P).ToList())}", g.Select(r =>r.P.SourceId ?? "(no image id)").ToList()));
  }



  private static void MergeInto(List<Row> g, Buckets b)
  {
    var keep = Survivor(g);
    b.Printings.Add(keep);
    if (g.Count > 1)
    {
      b.Merged += g.Count - 1;
      b.MergedGroups.Add(new MergedGroup(CatalogIssueRepository.IssueKeyOf(PrintingRepository.KeyOf(keep)), keep.SourceId ?? "(no image id)", g.Count));
    }
  }

  private static Printing Survivor(List<Row> g) => g.Select(r => r.P).OrderBy( p => p.ImageUri ?? "", StringComparer.Ordinal).First();

  private static bool IsRarityTrSplit(List<Row> rows, out List<Row> nonTr, out List<Row> tr)
  {
    tr    = rows.Where(r => r.P.Rarity == "TR").ToList();
    nonTr = rows.Where(r => r.P.Rarity != "TR").ToList();

    var first      = rows[0].P;
    var nonTrLocal = nonTr;

    return tr.Count > 0 && nonTr.Count > 0 
      && rows.All(r => r.P.Name     == first.Name
                    && r.P.CardType == first.CardType
                    && r.P.SourceId == first.SourceId)
      && nonTr.All(r => SameStoredIdentity(r.P, nonTrLocal[0].P));
  }

  private static Row Rekey(Row r, string printVariant, string variantKey)
  {
    r.P.PrintVariant = printVariant;
    r.P.VariantKey   = variantKey;

    return r;
  }

  private static bool SameStoredIdentity(Printing a, Printing b) => 
    a.Name     == b.Name
    && a.CardType == b.CardType
    && a.Rarity   == b.Rarity 
    && a.SourceId == b.SourceId;

  private static  string Differences(List<Printing> rows)
  {
    var fields = new (string Label, Func<Printing, string?> Get)[]
    {
      ("name",      p => p.Name),
      ("card_type", p => p.CardType),
      ("rarity",    p => p.Rarity),
      ("source_id", p => p.SourceId),
    };

    var parts = fields 
      .Select(f => (f.Label, Values: rows.Select(f.Get).Distinct().ToList()))
      .Where(f => f.Values.Count > 1)
      .Select(f => $"{f.Label}: {string.Join(" | ", f.Values.Select(v => $"'{v}'"))}");

    return String.Join("; ", parts);
  }

  private async Task<JsonDocument> FetchArrayAsync(string url, Action<string>? progress)
  {
    using var response = await HttpRetry.SendAsync(() => _http.GetAsync(url), $"optcgapi {url}", progress);
    var body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode) 
      throw new HttpRequestException($"optcgapi returned {(int)response.StatusCode} for {url}: {Preview(body)}");

    var doc = JsonDocument.Parse(body);

    var kind = doc.RootElement.ValueKind;
    if (kind != JsonValueKind.Array)
    {
      doc.Dispose();
      throw new InvalidOperationException(
        $"optcgapi {url} returned {kind}, not an array: {Preview(body)}");
    }
    
    return doc;
  }

  private static string Preview(string body) => body.Length <= 200 ? body : body[..200] + "...";

  private static string ReadString(JsonElement el, string prop, string fallback) => 
    el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
      ? v.GetString() ?? fallback
      : fallback;
}
