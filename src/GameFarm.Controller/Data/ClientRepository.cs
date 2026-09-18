using System.Text.Json;
using GameFarm.Core.Configuration;
using GameFarm.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GameFarm.Controller.Data;

/// <summary>
/// Lightweight SQLite-backed store for <see cref="GameClientInstance"/> records. Uses
/// Microsoft.Data.Sqlite directly (no ORM) — the schema is tiny and this keeps the dependency
/// footprint small; swap for EF Core later if the schema grows significantly.
/// </summary>
public sealed class ClientRepository : IClientRepository
{
    private readonly string _connectionString;

    public ClientRepository(IOptions<GameFarmOptions> options)
    {
        var dbPath = options.Value.DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Clients (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                VmName TEXT NOT NULL UNIQUE,
                SteamAccountName TEXT NULL,
                SteamId64 INTEGER NULL,
                ServerAddress TEXT NOT NULL,
                ServerPort INTEGER NOT NULL,
                ServerPasswordSecretName TEXT NULL,
                AutoStart INTEGER NOT NULL DEFAULT 0,
                AutoReconnect INTEGER NOT NULL DEFAULT 1,
                AutoUpdate INTEGER NOT NULL DEFAULT 1,
                RequiredModsJson TEXT NOT NULL DEFAULT '[]',
                CreatedUtc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<int> AddAsync(GameClientInstance instance, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Clients (Name, VmName, SteamAccountName, SteamId64, ServerAddress, ServerPort,
                ServerPasswordSecretName, AutoStart, AutoReconnect, AutoUpdate, RequiredModsJson, CreatedUtc)
            VALUES ($name, $vm, $acct, $sid, $addr, $port, $secret, $autostart, $autoreconnect, $autoupdate, $mods, $created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$name", instance.Name);
        cmd.Parameters.AddWithValue("$vm", instance.VmName);
        cmd.Parameters.AddWithValue("$acct", (object?)instance.SteamAccountName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sid", (object?)instance.SteamId64 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addr", instance.ServerAddress);
        cmd.Parameters.AddWithValue("$port", instance.ServerPort);
        cmd.Parameters.AddWithValue("$secret", (object?)instance.ServerPasswordSecretName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$autostart", instance.AutoStart ? 1 : 0);
        cmd.Parameters.AddWithValue("$autoreconnect", instance.AutoReconnect ? 1 : 0);
        cmd.Parameters.AddWithValue("$autoupdate", instance.AutoUpdate ? 1 : 0);
        cmd.Parameters.AddWithValue("$mods", JsonSerializer.Serialize(instance.RequiredMods));
        cmd.Parameters.AddWithValue("$created", instance.CreatedUtc.ToString("O"));

        var id = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        return (int)id;
    }

    public async Task<IReadOnlyList<GameClientInstance>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Clients ORDER BY Name;";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<GameClientInstance>();
        while (await reader.ReadAsync(ct))
            list.Add(Map(reader));
        return list;
    }

    public async Task<GameClientInstance?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Clients WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<GameClientInstance?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Clients WHERE Name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Clients WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAsync(GameClientInstance instance, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Clients SET SteamAccountName=$acct, SteamId64=$sid, ServerAddress=$addr, ServerPort=$port,
                ServerPasswordSecretName=$secret, AutoStart=$autostart, AutoReconnect=$autoreconnect,
                AutoUpdate=$autoupdate, RequiredModsJson=$mods WHERE Id=$id;
            """;
        cmd.Parameters.AddWithValue("$acct", (object?)instance.SteamAccountName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sid", (object?)instance.SteamId64 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addr", instance.ServerAddress);
        cmd.Parameters.AddWithValue("$port", instance.ServerPort);
        cmd.Parameters.AddWithValue("$secret", (object?)instance.ServerPasswordSecretName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$autostart", instance.AutoStart ? 1 : 0);
        cmd.Parameters.AddWithValue("$autoreconnect", instance.AutoReconnect ? 1 : 0);
        cmd.Parameters.AddWithValue("$autoupdate", instance.AutoUpdate ? 1 : 0);
        cmd.Parameters.AddWithValue("$mods", JsonSerializer.Serialize(instance.RequiredMods));
        cmd.Parameters.AddWithValue("$id", instance.Id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static GameClientInstance Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(reader.GetOrdinal("Id")),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        VmName = reader.GetString(reader.GetOrdinal("VmName")),
        SteamAccountName = reader.IsDBNull(reader.GetOrdinal("SteamAccountName")) ? null : reader.GetString(reader.GetOrdinal("SteamAccountName")),
        SteamId64 = reader.IsDBNull(reader.GetOrdinal("SteamId64")) ? null : (ulong)reader.GetInt64(reader.GetOrdinal("SteamId64")),
        ServerAddress = reader.GetString(reader.GetOrdinal("ServerAddress")),
        ServerPort = reader.GetInt32(reader.GetOrdinal("ServerPort")),
        ServerPasswordSecretName = reader.IsDBNull(reader.GetOrdinal("ServerPasswordSecretName")) ? null : reader.GetString(reader.GetOrdinal("ServerPasswordSecretName")),
        AutoStart = reader.GetInt32(reader.GetOrdinal("AutoStart")) == 1,
        AutoReconnect = reader.GetInt32(reader.GetOrdinal("AutoReconnect")) == 1,
        AutoUpdate = reader.GetInt32(reader.GetOrdinal("AutoUpdate")) == 1,
        RequiredMods = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("RequiredModsJson"))) ?? new(),
        CreatedUtc = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedUtc")))
    };
}
