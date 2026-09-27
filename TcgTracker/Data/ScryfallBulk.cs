using System.IO.Compression;
using System.Text.Json;

namespace TcgTracker.Data;

public class ScryfallBulk
{
  private const string BulkIndexUrl = "https://api.scryfall.com/bulk-data";

  private const string BulkType = "default_cards";

  private readonly HttpClient _http;
  private readonly string _cacheDir;

  public ScryfallBulk(HttpClient http, string cacheDir)
  {
    _http = http;
    _cacheDir = cacheDir;
  }

  public async Task<string> EnsureFileAsync(Action<string>? log = null)
  {
    Directory.CreateDirectory(_cacheDir);

    var filePath = Path.Combine(_cacheDir, $"scryfall-{BulkType}.jsonl");
    var versionPath = filePath + ".version";

    var (downloadUri, updatedAt) = await ReadBulkIndexAsync();

    var localVersion = File.Exists(versionPath) && File.Exists(filePath)
      ? await File.ReadAllTextAsync(versionPath)
      : null;

    if (localVersion == updatedAt)
    {
      log?.Invoke($" bulk file current ({updatedAt})");
      return filePath;
    }

    log?.Invoke($" downloading bulk file ({updatedAt})...");

    var tempPath = filePath + ".tmp";
    using (var response = await _http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead))
    {
      response.EnsureSuccessStatusCode();

      await using var source = await response.Content.ReadAsStreamAsync();
      await using var dest = File.Create(tempPath);
      await source.CopyToAsync(dest);
    }

    File.Move(tempPath, filePath, overwrite: true);

    await File.WriteAllTextAsync(versionPath, updatedAt);

    return filePath;
  }

  public static async IAsyncEnumerable<Printing> StreamPrintingsAsync(string filePath)
  {
    await using var file = File.OpenRead(filePath);
    await using var json = await OpenMaybeGzipAsync(file);
    using var reader = new StreamReader(json);

    while (await reader.ReadLineAsync() is {} line)
    {
      if (line.Length == 0) continue;

      using var doc = JsonDocument.Parse(line);

      foreach (var printing in ScryfallSource.MapCard(doc.RootElement, rawJson: null)) yield return printing;
    }
  }

  private static async Task<Stream> OpenMaybeGzipAsync(Stream raw)
  {
    var header = new byte[2];
    var read = await raw.ReadAsync(header.AsMemory(0, 2));
    raw.Seek(0, SeekOrigin.Begin);

    return read == 2 && header[0] == 0x1F && header[1] == 0x8B
      ? new GZipStream(raw, CompressionMode.Decompress)
      : raw;
  }

  private async Task<(string DownloadUri, string UpdatedAt)> ReadBulkIndexAsync()
  {

    var body = await _http.GetStringAsync(BulkIndexUrl);
    using var doc = JsonDocument.Parse(body);

    foreach (var entry in doc.RootElement.GetProperty("data").EnumerateArray())
    {
      if (entry.GetProperty("type").GetString() != BulkType) continue;

      if (!entry.TryGetProperty("jsonl_download_uri", out var uri)) throw new InvalidOperationException(
          $"Scryfall '{BulkType}' entry has no .jsonl_download_uri. Entry: {entry.GetRawText()}");

      return (uri.GetString()!, entry.GetProperty("updated_at").GetString()!);
    }

    throw new InvalidOperationException($"Scryfall bulk index has no '{BulkType}' entry");
  }
}