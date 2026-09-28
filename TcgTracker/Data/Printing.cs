namespace TcgTracker.Data;

public class Printing
{
  public string Game            {get;set;} = "";
  public string Name            {get;set;} = "";
  public string CardType        {get;set;} = "none";
  public string SetCode         {get;set;} = "";
  public string CollectorNumber {get;set;} = "";
  public string Finish          {get;set;} = "normal";
  public string Rarity          {get;set;} = "none";
  public string? ImageUri       {get;set;}

  public string PrintVariant    {get;set;} = "none";
  public string VariantKey      {get;set;} = "none";

  public string? SourceId       {get;set;}
  public string? SourceName     {get;set;}
  public int? TcgplayerId       {get;set;}
  public int? CardmarketId      {get;set;}

  public string PkmnPromoStamp  {get;set;} = "none";
  public string OpParallelType  {get;set;} = "none";
  public string MtgFrameStyle   {get;set;} = "none";

  public string? RawJson        {get;set;}
}