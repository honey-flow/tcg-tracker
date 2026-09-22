using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public record PrintingKey(
  string Game,
  string SetCode,
  string CollectorNumber,
  string Finish,
  string PrintVariant,
  string PkmnPromoStamp,
  string OpParallelType,
  string MtgFrameStyle
);

public record StoredPrinting(
  long    PrintingId,
  string  Name,
  string  CardType,
  string  Rarity,
  string? ImageUri,
  int?    TcgplayerId,
  int?    CardmarketId,
  string? SourceId,
  string? SourceName
);

public class PrintingRepository
{
  /** Single Card Path **/
  public long Insert(SqliteConnection conn, Printing p, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;

    cmd.CommandText = @"
      INSERT INTO printings (
        game, name, card_type, set_code, collector_number, finish,
        rarity, image_uri, print_variant, source_id, source_name,
        tcgplayer_id, cardmarket_id, raw_json,
        pkmn_promo_stamp, op_parallel_type, mtg_frame_style)
      VALUES (
        $game, $name, $card_type, $set_code, $collector_number, $finish,
        $rarity, $image_uri, $print_variant, $source_id, $source_name,
        $tcgplayer_id, $cardmarket_id, $raw_json,
        $pkmn_promo_stamp, $op_parallel_type, $mtg_frame_style)
      RETURNING printing_id;";
      
    BindPrinting(cmd, p);
    return Convert.ToInt64(cmd.ExecuteScalar());
  }

  public long? FindId(SqliteConnection conn, Printing p, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      SELECT printing_id FROM printings
      WHERE game             = $game
        AND set_code         = $set_code
        AND collector_number = $collector_number
        AND finish           = $finish
        AND print_variant    = $print_variant
        AND pkmn_promo_stamp = $pkmn_promo_stamp
        AND op_parallel_type = $op_parallel_type
        AND mtg_frame_style  = $mtg_frame_style;";
      
    cmd.Parameters.AddWithValue("$game",             p.Game);
    cmd.Parameters.AddWithValue("$set_code",         p.SetCode);
    cmd.Parameters.AddWithValue("$collector_number", p.CollectorNumber);
    cmd.Parameters.AddWithValue("$finish",           p.Finish);
    cmd.Parameters.AddWithValue("$print_variant",    p.PrintVariant);
    cmd.Parameters.AddWithValue("$pkmn_promo_stamp", p.PkmnPromoStamp);
    cmd.Parameters.AddWithValue("$op_parallel_type", p.OpParallelType);
    cmd.Parameters.AddWithValue("$mtg_frame_style",  p.MtgFrameStyle);

    var result = cmd.ExecuteScalar();
    return result is null or DBNull ? null : Convert.ToInt64(result);
  }

  public void Update(SqliteConnection conn, long printingId, Printing p, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      UPDATE printings SET
        name          = $name,
        card_type     = $card_type,
        rarity        = $rarity,
        image_uri     = $image_uri,
        source_id     = $source_id,
        source_name   = $source_name,
        tcgplayer_id  = $tcgplayer_id,
        cardmarket_id = $cardmarket_id,
        raw_json      = $raw_json
      WHERE printing_id = $printing_id;";
      
    cmd.Parameters.AddWithValue("$printing_id",   printingId);
    cmd.Parameters.AddWithValue("$name",          p.Name);
    cmd.Parameters.AddWithValue("$card_type",     p.CardType);
    cmd.Parameters.AddWithValue("$rarity",        p.Rarity);
    cmd.Parameters.AddWithValue("$image_uri",     (object?)p.ImageUri ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$source_id",     (object?)p.SourceId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$source_name",   (object?)p.SourceName ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$tcgplayer_id",  (object?)p.TcgplayerId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$cardmarket_id", (object?)p.CardmarketId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$raw_json",      (object?)p.RawJson ?? DBNull.Value);

    cmd.ExecuteNonQuery();
  }

  /** Bulk Path **/
  public Dictionary<PrintingKey, StoredPrinting> LoadAllKeys(SqliteConnection conn)
  {
    var map = new Dictionary<PrintingKey, StoredPrinting>();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT printing_id, game, set_code, collector_number, finish,
             print_variant, pkmn_promo_stamp, op_parallel_type, mtg_frame_style,
             name, card_type, rarity, image_uri,
             tcgplayer_id, cardmarket_id, source_id, source_name
      FROM printings;";

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      var key = new PrintingKey(
        reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6),
        reader.GetString(7), reader.GetString(8));

      map[key] = new StoredPrinting(
        reader.GetInt64(0),
        reader.GetString(9),
        reader.GetString(10),
        reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetInt32(13),
        reader.IsDBNull(14) ? null : reader.GetInt32(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16));
    }
    return map;
  }

  public Dictionary<(string Source, string Id, string Finish), long> LoadSourceIds(SqliteConnection conn)
  {
    var map = new Dictionary<(string, string, string), long>();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT printing_id, source_name, source_id, finish FROM printings
      WHERE source_name IS NOT NULL AND source_id IS NOT NULL AND source_id <> '';";

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      map[(reader.GetString(1), reader.GetString(2), reader.GetString(3))] = reader.GetInt64(0);

    return map;
  }

  public static PrintingKey KeyOf(Printing p) => new(
    p.Game, p.SetCode, p.CollectorNumber, p.Finish, 
    p.PrintVariant, p.PkmnPromoStamp, p.OpParallelType, p.MtgFrameStyle
  );

  private static void BindPrinting(SqliteCommand cmd, Printing p)
  {
    cmd.Parameters.AddWithValue("$game",                      p.Game);
    cmd.Parameters.AddWithValue("$name",                      p.Name);
    cmd.Parameters.AddWithValue("$card_type",                 p.CardType);
    cmd.Parameters.AddWithValue("$set_code",                  p.SetCode);
    cmd.Parameters.AddWithValue("$collector_number",          p.CollectorNumber);
    cmd.Parameters.AddWithValue("$finish",                    p.Finish);
    cmd.Parameters.AddWithValue("$rarity",                    p.Rarity);
    cmd.Parameters.AddWithValue("$image_uri",        (object?)p.ImageUri     ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$print_variant",             p.PrintVariant);
    cmd.Parameters.AddWithValue("$source_id",        (object?)p.SourceId     ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$source_name",      (object?)p.SourceName   ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$tcgplayer_id",     (object?)p.TcgplayerId  ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$cardmarket_id",    (object?)p.CardmarketId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$raw_json",         (object?)p.RawJson      ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$pkmn_promo_stamp",          p.PkmnPromoStamp);
    cmd.Parameters.AddWithValue("$op_parallel_type",          p.OpParallelType);
    cmd.Parameters.AddWithValue("$mtg_frame_style",           p.MtgFrameStyle);
  }
}