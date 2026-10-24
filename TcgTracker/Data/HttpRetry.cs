using System.Net;

namespace TcgTracker.Data;

public static class HttpRetry
{
  private static readonly TimeSpan[] Delays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8) };

  public static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, string what, Action<string>? progress = null)
  {
    for (var attempt = 1; ; attempt++)
    {
      var canRetry = attempt <= Delays.Length;
      string reason;

      try
      {
        var response = await send();

        if (!IsTransient(response.StatusCode) || !canRetry)
          return response;

        reason = $"{(int)response.StatusCode} {response.ReasonPhrase}";

        response.Dispose();
      }
      catch (HttpRequestException ex) when (canRetry && ex.StatusCode is null)
      {
        reason = ex.Message;
      }
      catch (TaskCanceledException ex) when (canRetry && ex.InnerException is TimeoutException)
      {
        reason = "timed out";
      }

      var delay = Delays[attempt - 1];
      progress?.Invoke(
        $"  {what}: {reason}; retrying in {delay.TotalSeconds:N0}s " +
        $"(attempt {attempt + 1} of {Delays.Length + 1})");

      await Task.Delay(delay);
    }
  }

  private static bool IsTransient(HttpStatusCode code) => (int)code >= 500 || code == HttpStatusCode.RequestTimeout;
}