using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public class CopyRepository
{
  private const string Columns = @"
    copy_id, printing_id, location_id, condition, grading_company, grade,
    cert_number, status, supersedes_copy_id, submission_id, sealed_purchase_id,
    acquisition_price_cents, acquisition_date, grading_fee_cents,
    sale_price_cents, sale_date";

  public long Insert(SqliteConnection conn, Copy c, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      INSERT into copies (
        printing_id, location_id, condition, grading_company, grade, cert_number,
        status, supersedes_copy_id, submission_id, sealed_purchase_id, acquisition_price_cents,
        acquisition_date, grading_fee_cents, sale_price_cents, sale_date)
      VALUES (
        $printing_id, $location_id, $condition, $grading_company, $grade, $cert_number,
        $status, $supersedes_copy_id, $submission_id, $sealed_purchase_id, $acquisition_price_cents,
        $acquisition_date, $grading_fee_cents, $sale_price_cents, $sale_date)
      RETURNING copy_id";

    cmd.Parameters.AddWithValue("$printing_id",             c.PrintingId               );
    cmd.Parameters.AddWithValue("$location_id",             c.LocationId               );
    cmd.Parameters.AddWithValue("$condition",               ToDb(c.Condition)          );
    cmd.Parameters.AddWithValue("$grading_company",         Db(c.GradingCompany)       );
    cmd.Parameters.AddWithValue("$grade",                   Db(c.Grade)                );
    cmd.Parameters.AddWithValue("$cert_number",             Db(c.CertNumber)           );
    cmd.Parameters.AddWithValue("$status",                  ToDb(c.Status)             );
    cmd.Parameters.AddWithValue("$supersedes_copy_id",      Db(c.SupersedesCopyId)     );
    cmd.Parameters.AddWithValue("$submission_id",           Db(c.SubmissionId)         );
    cmd.Parameters.AddWithValue("$sealed_purchase_id",      Db(c.SealedPurchaseId)     );
    cmd.Parameters.AddWithValue("$acquisition_price_cents", Db(c.AcquisitionPriceCents));
    cmd.Parameters.AddWithValue("$acquisition_date",        Db(ToDb(c.AcquisitionDate)));
    cmd.Parameters.AddWithValue("$grading_fee_cents",       Db(c.GradingFeeCents)      );
    cmd.Parameters.AddWithValue("$sale_price_cents",        Db(c.SalePriceCents)       );
    cmd.Parameters.AddWithValue("$sale_date",               Db(ToDb(c.SaleDate)  )     );

    var id = Convert.ToInt64(cmd.ExecuteScalar());
    c.CopyId = id;
    return id;
  }

  public Copy? GetById(SqliteConnection conn, long copyId, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    cmd.CommandText = $"SELECT {Columns} FROM copies WHERE copy_id = $id;";
    cmd.Parameters.AddWithValue("$id", copyId);
    using var reader = cmd.ExecuteReader();
    return reader.Read() ? ReadCopy(reader) : null;
  }

  public List<Copy> GetAllOwned(SqliteConnection conn, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"SELECT {Columns} from copies WHERE status = 'owned';";

    var list = new List<Copy>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      list.Add(ReadCopy(reader));
    return list;
  }

  private static Copy ReadCopy(SqliteDataReader r) => new()
  {
    CopyId                = r.GetInt64(0),
    PrintingId            = r.GetInt64(1),
    LocationId            = r.GetInt64(2),
    Condition             = ConditionFromDb(r.GetString(3)),
    GradingCompany        = r.IsDBNull(4)  ? null : r.GetString(4),
    Grade                 = r.IsDBNull(5)  ? null : r.GetDouble(5),
    CertNumber            = r.IsDBNull(6)  ? null : r.GetString(6),
    Status                = StatusFromDb(r.GetString(7)),
    SupersedesCopyId      = r.IsDBNull(8)  ? null : r.GetInt64(8),
    SubmissionId          = r.IsDBNull(9)  ? null : r.GetInt64(9),
    SealedPurchaseId      = r.IsDBNull(10) ? null : r.GetInt64(10),
    AcquisitionPriceCents = r.IsDBNull(11) ? null : r.GetInt64(11),
    AcquisitionDate       = r.IsDBNull(12) ? null : DateFromDb(r.GetString(12)),
    GradingFeeCents       = r.IsDBNull(13) ? null : r.GetInt64(13),
    SalePriceCents        = r.IsDBNull(14) ? null : r.GetInt64(14),
    SaleDate              = r.IsDBNull(15) ? null : DateFromDb(r.GetString(15))
  };

  private static string ToDb(CopyStatus s) => s switch
  {
    CopyStatus.Owned    => "owned",
    CopyStatus.Sold     => "sold",
    CopyStatus.AtGrader => "at_grader",
    CopyStatus.Lost     => "lost",
    CopyStatus.Traded   => "traded",
    _                   => throw new ArgumentOutOfRangeException(nameof(s), s, null)
  };

  private static CopyStatus StatusFromDb(string s) => s switch
  {
    "owned"     => CopyStatus.Owned,
    "sold"      => CopyStatus.Sold,
    "at_grader" => CopyStatus.AtGrader,
    "lost"      => CopyStatus.Lost,
    "traded"    => CopyStatus.Traded,
    _           => throw new InvalidDataException($"Unknown copy status '{s}' in database.")
  };

  private static string ToDb(CardCondition c) => c switch
  {
    CardCondition.NM     => "NM",
    CardCondition.LP     => "LP",
    CardCondition.MP     => "MP",
    CardCondition.HP     => "HP",
    CardCondition.DMG    => "DMG",
    CardCondition.Graded => "graded",
    _                    => throw new ArgumentOutOfRangeException(nameof(c), c, null)
  };
  
  private static CardCondition ConditionFromDb(string s) => s switch
  {
    "NM"     => CardCondition.NM,
    "LP"     => CardCondition.LP,
    "MP"     => CardCondition.MP,
    "HP"     => CardCondition.HP,
    "DMG"    => CardCondition.DMG,
    "graded" => CardCondition.Graded,
    _        => throw new InvalidDataException($"Unknown condition '{s}' in database.")
  };

  private static string? ToDb(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);


  private static DateOnly DateFromDb(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);


  private static object Db(object? value) => value ?? DBNull.Value;
}