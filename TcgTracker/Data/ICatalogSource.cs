namespace TcgTracker.Data;

public interface ICatalogSource
{
  Task<IEnumerable<Printing>> GetPrintingAsync(CardRef card);
}