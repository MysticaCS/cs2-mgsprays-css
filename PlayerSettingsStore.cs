using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace MgSprays;

internal readonly record struct PlayerSettings(string? SprayName, int SprayVolume)
{
    public static PlayerSettings Default => new(null, 100);
}

internal sealed class PlayerSettingsStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<ulong, PlayerSettings> _cache = new();
    private readonly Channel<WriteRequest> _writes = Channel.CreateUnbounded<WriteRequest>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _worker;
    private sealed record WriteRequest(ulong SteamId, bool IsVolume, string? SprayName, int Volume,
        TaskCompletionSource Completion, string? ExpectedName);

    public PlayerSettingsStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        // Startup initialization only. Normal changes are handled by the worker.
        using var connection = Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS player_settings (
                steam_id TEXT NOT NULL PRIMARY KEY,
                spray_name TEXT NULL,
                spray_volume INTEGER NOT NULL DEFAULT 100 CHECK(spray_volume BETWEEN 0 AND 100)
            );
            """;
        setup.ExecuteNonQuery();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT steam_id, spray_name, spray_volume FROM player_settings;";
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            if (!ulong.TryParse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
                continue;
            int volume = reader.GetInt32(2);
            if (volume is >= 0 and <= 100)
                _cache[id] = new(reader.IsDBNull(1) ? null : reader.GetString(1), volume);
        }
        _worker = Task.Run(ProcessWrites);
    }

    public PlayerSettings Get(ulong steamId) => _cache.GetValueOrDefault(steamId, PlayerSettings.Default);

    public Task SetSprayAsync(ulong steamId, string? sprayName) => Queue(steamId, false, sprayName, 100);

    // A queued login reset must not overwrite a newer selection made before it runs.
    public Task ResetSprayIfMatchesAsync(ulong steamId, string expectedName) =>
        Queue(steamId, false, null, 100, expectedName);

    public Task SetVolumeAsync(ulong steamId, int volume)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        return Queue(steamId, true, null, volume);
    }

    private Task Queue(ulong steamId, bool isVolume, string? sprayName, int volume, string? expectedName = null)
    {
        if (steamId == 0) throw new ArgumentOutOfRangeException(nameof(steamId));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(new(steamId, isVolume, sprayName, volume, completion, expectedName)))
            completion.SetException(new ObjectDisposedException(nameof(PlayerSettingsStore)));
        return completion.Task;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private async Task ProcessWrites()
    {
        await foreach (var request in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = request.ExpectedName != null
                    ? "UPDATE player_settings SET spray_name = $value WHERE steam_id = $id AND spray_name = $expected;"
                    : request.IsVolume
                    ? """
                      INSERT INTO player_settings (steam_id, spray_volume) VALUES ($id, $value)
                      ON CONFLICT(steam_id) DO UPDATE SET spray_volume = excluded.spray_volume;
                      """
                    : """
                      INSERT INTO player_settings (steam_id, spray_name) VALUES ($id, $value)
                      ON CONFLICT(steam_id) DO UPDATE SET spray_name = excluded.spray_name;
                      """;
                command.Parameters.AddWithValue("$id", request.SteamId.ToString(CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$value", request.IsVolume ? request.Volume : (object?)request.SprayName ?? DBNull.Value);
                if (request.ExpectedName != null) command.Parameters.AddWithValue("$expected", request.ExpectedName);
                int changed = command.ExecuteNonQuery();
                if (request.ExpectedName != null && changed == 0)
                {
                    request.Completion.SetResult();
                    continue;
                }
                // Cache changes only after a committed write. Each UPSERT touches
                // one setting, preserving the player's other setting.
                var current = Get(request.SteamId);
                _cache[request.SteamId] = request.IsVolume
                    ? current with { SprayVolume = request.Volume }
                    : current with { SprayName = request.SprayName };
                request.Completion.SetResult();
            }
            catch (Exception e) { request.Completion.SetException(e); }
        }
    }

    public void Dispose()
    {
        _writes.Writer.TryComplete();
        // Drain accepted writes at unload/shutdown; no game callbacks occur here.
        _worker.GetAwaiter().GetResult();
    }
}
