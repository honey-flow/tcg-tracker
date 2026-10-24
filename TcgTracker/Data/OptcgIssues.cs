namespace TcgTracker.Data;

public static class OptcgIssues
{
  public const string Game   = "onepiece";
  public const string Source = "optcgapi";

  public static List<CatalogIssue> Build(OptcgBulkResult result, IReadOnlyDictionary<PrintingKey, StoredPrinting> stored)
  {
    var raw = new List<CatalogIssue>();

    foreach (var h in result.HeldOut)
      raw.Add(Issue("held_out", h.IssueKey, $"Listed by optcgapi but not stored: {h.Reason}", $"image ids: {string.Join(", ", h.ImageIds)}"));

    foreach (var m in result.MergedGroups)
      raw.Add(Issue("merged", m.IssueKey, $"optcgapi lists {m.ImageId} {m.Rows} times with identical data; stored once. " +
                    "If you own physically different versions, the app records them as one printing.", null));

    foreach (var c in result.Corrected)
      raw.Add(Issue("corrected", c.ImageId, $"optcgapi's card_set_id for {c.ImageId} is wrong; {c.CardSetIdUsed} was used instead.",
                    "Confirmed source error, listed in OptcgSource.Corrections."));

    var fetched = result.Printings.Select(PrintingRepository.KeyOf).ToHashSet();
    foreach (var (key, sp) in stored)
    {
      if (key.Game != Game || sp.SourceName != Source || fetched.Contains(key))
        continue;

      raw.Add(Issue("stale", CatalogIssueRepository.IssueKeyOf(key), $"'{sp.Name}' ({sp.SourceId}) is stored but optcgapi no longer lists it. " + 
                    "It may have been renamed or removed; check it before relying on it.", $"printing_id {sp.PrintingId}"));
    }

    return raw
      .GroupBy(i => (i.Kind, i.IssueKey))
      .Select(g => g.Count() == 1 ? g.First() : g.First() with
      {
        Detail = string.Join("; ", g.Select(i => i.Detail).Where(d => d is not null).Distinct())
      })
      .ToList();
  }

  private static CatalogIssue Issue(string kind, string key, string summary, string? detail) => new(Game, Source, kind, key, summary, detail);
}