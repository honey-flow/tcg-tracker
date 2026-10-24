using System.Globalization;

namespace TcgTracker.Data;

public static class Money
{
  public static bool TryParseCents(string text, out long cents)
  {
    cents = 0;

    if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
      return false;

    var inCents = amount * 100m;
    if (inCents != decimal.Truncate(inCents))
      return false;

    cents = (long)inCents;
    return true;
  }
}