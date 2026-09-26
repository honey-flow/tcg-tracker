namespace TcgTracker.Data;

public enum CopyStatus    { Owned, Sold, AtGrader, Lost, Traded };

public enum CardCondition { NM, LP, MP, HP, DMG, Graded         };

public static class KnownLocations
{
  public const long Unsorted = 1;
}

public class Copy
{
  public long?         CopyId                {get; set;} 
  public long          PrintingId            {get; set;}
  public long          LocationId            {get; set;} = KnownLocations.Unsorted;

  //----- Condition and Grading -----//
  public CardCondition Condition             {get; set;} = CardCondition.NM;
  public string?       GradingCompany        {get; set;}
  public double?        Grade                 {get; set;}
  public string?       CertNumber            {get; set;}

  //----- Lifecycle -----//
  public CopyStatus    Status                {get; set;} = CopyStatus.Owned;
  public long?         SupersedesCopyId      {get; set;}
  public long?         SubmissionId          {get; set;}
  public long?         SealedPurchaseId      {get; set;}

  // ----- Money ----- //
  public long?         AcquisitionPriceCents {get; set;}
  public DateOnly?     AcquisitionDate       {get; set;}
  public long?         GradingFeeCents       {get; set;}
  public long?         SalePriceCents        {get; set;}
  public DateOnly?     SaleDate              {get; set;}
}