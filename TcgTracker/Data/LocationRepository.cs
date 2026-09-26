using Microsoft.Data.Sqlite;

namespace TcgTracker.Data;

public class LocationRepository
{
  private const string Columns = @"location_id, parent_location_id, name, description, is_active";

  public InsertResult Insert(SqliteConnection conn, Location loc, SqliteTransaction? tx = null)
  {
    var existing = FindByName(conn, loc.ParentLocationId, loc.Name, tx);
    if (existing is not null) return new InsertResult(InsertOutcome.AlreadyExists, existing.LocationId!.Value);

    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      INSERT INTO locations (parent_location_id, name, description, is_active)
      VALUES ($parent_location_id, $name, $description, $is_active)
      RETURNING location_id;";
    
    cmd.Parameters.AddWithValue("$parent_location_id", Db(loc.ParentLocationId));
    cmd.Parameters.AddWithValue("$name",               loc.Name                );
    cmd.Parameters.AddWithValue("$description",        Db(loc.Description)     );
    cmd.Parameters.AddWithValue("$is_active",          loc.IsActive ? 1 : 0    );

    var id = Convert.ToInt64(cmd.ExecuteScalar());
    loc.LocationId = id;
    return new InsertResult(InsertOutcome.Created, id);
  }

  public void Edit(SqliteConnection conn, Location loc, SqliteTransaction? tx = null)
  {
    if (loc.LocationId is null)
      throw new ArgumentException("Cannot edit a location that has no ID.", nameof(loc));

    if (loc.LocationId == loc.ParentLocationId)
      throw new ArgumentException("A location cannot be its own parent.", nameof(loc));

    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      UPDATE locations SET
        parent_location_id = $parent_location_id,
        name               = $name              ,
        description        = $description
      WHERE location_id = $location_id;";

    cmd.Parameters.AddWithValue("$location_id",        loc.LocationId.Value    );
    cmd.Parameters.AddWithValue("$parent_location_id", Db(loc.ParentLocationId));
    cmd.Parameters.AddWithValue("$name",               loc.Name                );
    cmd.Parameters.AddWithValue("$description",        Db(loc.Description)     );

    cmd.ExecuteNonQuery();
  }

  public void SetActive(SqliteConnection conn, long locationId, bool active, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = @"
      UPDATE locations SET is_active = $is_active WHERE location_id = $id;";
    
    cmd.Parameters.AddWithValue("$id",        locationId    );
    cmd.Parameters.AddWithValue("$is_active", active ? 1 : 0);

    cmd.ExecuteNonQuery();
  }

  public List<Location> GetAll(SqliteConnection conn, bool includeInactive = false, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $@"
      SELECT {Columns} FROM locations
      WHERE is_active = 1 OR $include = 1
      ORDER BY parent_location_id NULLS FIRST , name;";
    
    cmd.Parameters.AddWithValue("$include", includeInactive ? 1 : 0);

    var list = new List<Location>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      list.Add(ReadLocation(reader));

    return list;
  }

  public Location? GetById(SqliteConnection conn, long locationId, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $@"
      SELECT {Columns} FROM locations WHERE location_id = $id;";

    cmd.Parameters.AddWithValue("$id", locationId);
    using var reader = cmd.ExecuteReader();
    return reader.Read() ? ReadLocation(reader) : null;
  }
  
  public List<Location> GetChildren(SqliteConnection conn, long? parentId, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $@"
     SELECT {Columns} FROM locations WHERE parent_location_id IS $parent ORDER BY name;";

    cmd.Parameters.AddWithValue("$parent", Db(parentId));

    var list = new List<Location>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      list.Add(ReadLocation(reader));

    return list;
  }

  public Location? FindByName(SqliteConnection conn, long? parentId, string name, SqliteTransaction? tx = null)
  {
    using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = $"SELECT {Columns} FROM locations WHERE parent_location_id IS $parent AND name = $name;";
    cmd.Parameters.AddWithValue("$parent", Db(parentId));
    cmd.Parameters.AddWithValue("$name",   name);


    using var reader = cmd.ExecuteReader();
    return reader.Read() ? ReadLocation(reader) : null;
  }

  private static Location ReadLocation(SqliteDataReader r) => new()
  {
    LocationId       = r.GetInt64(0)                        ,
    ParentLocationId = r.IsDBNull(1) ? null : r.GetInt64(1) ,
    Name             = r.GetString(2)                       ,
    Description      = r.IsDBNull(3) ? null : r.GetString(3),
    IsActive         = r.GetInt64(4) == 1
  };

  private static object Db(object? value) => value ?? DBNull.Value;
}