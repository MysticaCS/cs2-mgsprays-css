using MgSprays;
using Microsoft.Data.Sqlite;

int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
string folder = Path.Combine(AppContext.BaseDirectory, "sqlite-test-" + Guid.NewGuid().ToString("N"));
string db = Path.Combine(folder, "data", "mgsprays.db");
using (var settings = new PlayerSettingsStore(db))
{
    Check(File.Exists(db), "Database automatically created");
    Check(settings.Get(123) == PlayerSettings.Default, "New player defaults to random and 100%");
    await settings.SetSprayAsync(123, "test");
    Check(settings.Get(123) == new PlayerSettings("test", 100), "First selection preserves default volume");
    await settings.SetVolumeAsync(123, 35);
    Check(settings.Get(123) == new PlayerSettings("test", 35), "Volume update preserves selection");
    await settings.SetSprayAsync(123, "charlotte_heart");
    Check(settings.Get(123) == new PlayerSettings("charlotte_heart", 35), "Selection update preserves volume");
    await settings.SetVolumeAsync(456, 0);
    Check(settings.Get(456) == new PlayerSettings(null, 0), "Muted new player remains random");
    Check(settings.Get(123).SprayVolume == 35, "Independent player records");
    await settings.SetSprayAsync(123, null);
    Check(settings.Get(123) == new PlayerSettings(null, 35), "Random clears selection while preserving volume");
    string hostileId = "test'); DROP TABLE player_settings; --";
    await settings.SetSprayAsync(789, hostileId);
    Check(settings.Get(789).SprayName == hostileId, "Selection strings are parameterized");
    var pending = new List<Task>();
    for (int i = 0; i < 20; i++)
    {
        pending.Add(settings.SetSprayAsync(1000, "spray" + i));
        pending.Add(settings.SetVolumeAsync(1000, i));
    }
    await Task.WhenAll(pending);
    Check(settings.Get(1000) == new PlayerSettings("spray19", 19), "Queued writes preserve order and both fields");
    await settings.SetSprayAsync(ulong.MaxValue, "max-id");
    await settings.SetVolumeAsync(ulong.MaxValue, 100);
    Check(settings.Get(ulong.MaxValue).SprayName == "max-id", "SteamID64 retains unsigned precision");
    bool badVolume = false;
    try { await settings.SetVolumeAsync(123, 101); } catch (ArgumentOutOfRangeException) { badVolume = true; }
    Check(badVolume && settings.Get(123).SprayVolume == 35, "Invalid volume rejected without change");
    bool badSteam = false;
    try { await settings.SetSprayAsync(0, "test"); } catch (ArgumentOutOfRangeException) { badSteam = true; }
    Check(badSteam, "Invalid SteamID rejected");
    await settings.SetVolumeAsync(321, 60);
    await settings.SetSprayAsync(321, "deleted_image");
    await settings.ResetSprayIfMatchesAsync(321, "deleted_image");
    Check(settings.Get(321) == new PlayerSettings(null, 60), "Missing selection becomes random and preserves volume");
    await settings.SetSprayAsync(321, "new_image");
    await settings.ResetSprayIfMatchesAsync(321, "deleted_image");
    Check(settings.Get(321) == new PlayerSettings("new_image", 60), "Stale reset does not overwrite newer selection");
    await settings.SetSprayAsync(321, "deleted_image");
    Task chooseNew = settings.SetSprayAsync(321, "valid_image");
    Task staleReset = settings.ResetSprayIfMatchesAsync(321, "deleted_image");
    await Task.WhenAll(chooseNew, staleReset);
    Check(settings.Get(321) == new PlayerSettings("valid_image", 60), "Queued login reset is conditional after newer write");
    await settings.ResetSprayIfMatchesAsync(9999, "missing");
    Check(settings.Get(9999) == PlayerSettings.Default, "Reset of nonexistent preference is harmless");
    await settings.ResetSprayIfMatchesAsync(321, "valid_image");
    Check(settings.Get(321).SprayName == null && settings.Get(321).SprayVolume == 60, "All-images-removed reset preserves audio preference");
    // A conflicting SQLite writer must fail without changing cached settings.
    using var locked = new SqliteConnection($"Data Source={db};Pooling=False");
    locked.Open();
    using (var transaction = locked.BeginTransaction())
    {
        bool failed = false;
        try { await settings.SetVolumeAsync(123, 80); } catch (SqliteException) { failed = true; }
        Check(failed && settings.Get(123).SprayVolume == 35, "Locked write fails without false success/cache change");
        transaction.Rollback();
    }
    await settings.SetVolumeAsync(123, 45);
    Check(settings.Get(123).SprayVolume == 45, "Queue recovers after a failed write");
}
using (var reopened = new PlayerSettingsStore(db))
{
    Check(reopened.Get(123) == new PlayerSettings(null, 45), "Persisted settings survive reopening");
    Check(reopened.Get(321) == new PlayerSettings(null, 60), "Automatic random reset persists after reopening");
    Check(reopened.Get(456).SprayVolume == 0, "Mute survives reopening");
    Check(reopened.Get(ulong.MaxValue).SprayName == "max-id", "Full SteamID64 survives reopening");
    // Dispose must finish accepted changes before the next plugin instance loads.
    _ = reopened.SetSprayAsync(2000, "shutdown-write");
}
using (var afterUnload = new PlayerSettingsStore(db))
    Check(afterUnload.Get(2000).SprayName == "shutdown-write", "Unload drains accepted writes");
using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
{
    connection.Open();
    using var check = connection.CreateCommand();
    check.CommandText = "SELECT typeof(steam_id), spray_name, spray_volume FROM player_settings WHERE steam_id = $id;";
    check.Parameters.AddWithValue("$id", ulong.MaxValue.ToString());
    using var reader = check.ExecuteReader();
    Check(reader.Read() && reader.GetString(0) == "text" && reader.GetString(1) == "max-id" && reader.GetInt32(2) == 100,
        "Actual SQLite schema and stored fields verified");
}
Console.WriteLine($"Passed {checks} SQLite integration checks with the real native SQLite library.");
Console.WriteLine($"Test database: {db}");

