namespace TcgTracker.Data;

public class Location
{
  public long?   LocationId       {get; set;}
  public long?   ParentLocationId {get; set;} 
  public string  Name             {get; set;}                               = ""  ;
  public string? Description      {get; set;}
  public bool    IsActive         {get; set;}                               = true;
}

public enum   InsertOutcome {Created, AlreadyExists};
public record InsertResult(InsertOutcome Outcome, long locationId);