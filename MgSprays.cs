using System.Globalization;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Cvars;

namespace MgSprays;

public sealed class SprayDefinition
{
    public string Id { get; set; } = "sample";
    public string Name { get; set; } = "Sample";
    public string Material { get; set; } = "materials/mgsprays/sample.vmat";
    public float Width { get; set; } = 48;
    public float Height { get; set; } = 48;
}

public sealed class SprayConfig : BasePluginConfig
{
    public string SpraySoundEvent { get; set; } = "";
    public string SpraySoundResource { get; set; } = "";
    public List<SprayDefinition> Sprays { get; set; } = [new()];
}

[MinimumApiVersion(376)]
public sealed class MgSpraysPlugin : BasePlugin, IPluginConfig<SprayConfig>
{
    public override string ModuleName => "MG Sprays";
    public override string ModuleVersion => "1.0.2";
    public override string ModuleDescription => "Workshop sprays with per-player preferences and sound volume";
    public SprayConfig Config { get; set; } = new();
    private PlayerSettingsStore _settings = null!;
    private sealed class ActiveSpray(SpraySnapshot snapshot)
    {
        public SprayVisual<CEnvDecal> Visual { get; } = new(snapshot);
        public CounterStrikeSharp.API.Modules.Timers.Timer? Expiry { get; set; }
    }
    private readonly Dictionary<ulong, List<ActiveSpray>> _owned = new();
    private readonly Dictionary<ulong, double> _lastUsed = new();
    private readonly List<ActiveSpray> _active = new();
    private CounterStrikeSharp.API.Modules.Timers.Timer? _repairTimer;
    private int _mapGeneration;
    private readonly FakeConVar<bool> _enabled = new("lp_mgspray_enabled", "Enable MG sprays", true);
    private readonly FakeConVar<float> _lifetime = new("lp_mgspray_lifetime_seconds", "Spray lifetime in seconds", 120);
    private readonly FakeConVar<float> _maxDistance = new("lp_mgspray_max_distance", "Maximum spray distance", 128);
    private readonly FakeConVar<float> _depth = new("lp_mgspray_depth", "Decal projection depth", 4);
    private readonly FakeConVar<int> _maxActive = new("lp_mgspray_max_active", "Maximum simultaneous sprays", 64);
    private readonly FakeConVar<int> _maxPerPlayer = new("lp_mgspray_max_per_player", "Maximum sprays per player", 1);
    private readonly FakeConVar<float> _cooldown = new("lp_mgspray_cooldown_seconds", "Cooldown after a successful spray in seconds", 0);
    private readonly FakeConVar<float> _pitchOffset = new("lp_mgspray_pitch_offset", "Decal pitch offset in degrees", 0);
    private readonly FakeConVar<float> _yawOffset = new("lp_mgspray_yaw_offset", "Decal yaw offset in degrees", 0);
    private readonly FakeConVar<float> _rollOffset = new("lp_mgspray_roll_offset", "Decal roll offset in degrees", 0);
    private readonly FakeConVar<bool> _debug = new("lp_mgspray_debug", "Detailed server logging", true);
    private readonly FakeConVar<bool> _chatEnabled = new("lp_mgspray_chat_enabled", "Spray success chat notification", true);
    private readonly FakeConVar<bool> _soundEnabled = new("lp_mgspray_sound_enabled", "Spray sounds for all players", true);
    private readonly FakeConVar<bool> _keepAcrossRounds = new("lp_mgspray_keep_across_rounds", "Keep sprays until expiry across rounds", true);
    // Core-library strings survive plugin hot reload without retaining plugin types.
    // This state exists only in the current server process, never on disk.
    private const string RuntimeStateKey = "MgSprays.RuntimeCvars.v040";
    private Dictionary<string, string> _runtimeValues = new(StringComparer.Ordinal);
    private volatile bool _loaded;
    private string DatabasePath => Path.Combine(ModuleDirectory, "data", "mgsprays.db");

    public void OnConfigParsed(SprayConfig config)
    {
        if (config.Sprays == null || config.Sprays.Count > 128
            || config.Sprays.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name)
                || string.IsNullOrWhiteSpace(s.Material) || !s.Material.StartsWith("materials/", StringComparison.Ordinal)
                || !s.Material.EndsWith(".vmat", StringComparison.Ordinal) || s.Material.Contains("..") || s.Material.Contains('\\')
                || !CvarRules.InRange(s.Width, 1, 256) || !CvarRules.InRange(s.Height, 1, 256)
                || s.Id.Equals("none", StringComparison.OrdinalIgnoreCase))
            || config.Sprays.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Sprays.Count)
            throw new ArgumentException("MG Sprays: invalid or duplicate spray definitions; use materials/.../*.vmat.");
        Config = config;
        if (_loaded) ValidateOnlineSelections();
    }

    public override void Load(bool hotReload)
    {
        _settings = new PlayerSettingsStore(DatabasePath);
        AddCommand("css_sprays", "List sprays, or select using css_sprays <id>", Select);
        AddCommand("css_spray", "Spray a Workshop material on the aimed world surface", Spray);
        AddCommand("css_sprayvol", "Set your spray sound volume (0..100)", SetVolume);
        bool restoreRuntime = AppContext.GetData(RuntimeStateKey) is Dictionary<string, string>;
        _runtimeValues = AppContext.GetData(RuntimeStateKey) as Dictionary<string, string>
            ?? new(StringComparer.Ordinal);
        RegisterCvars();
        AppContext.SetData(RuntimeStateKey, _runtimeValues);
        _loaded = true;
        if (!restoreRuntime)
        {
            // Execute once after registration; never execute from OnMapStart.
            Server.NextWorldUpdate(() =>
            {
                if (_loaded) Server.ExecuteCommand("exec mgsprays/mgsprays.cfg");
            });
        }
        RegisterListener<Listeners.OnServerPrecacheResources>(manifest =>
        {
            foreach (var spray in Config.Sprays) manifest.AddResource(spray.Material);
            if (!string.IsNullOrWhiteSpace(Config.SpraySoundResource))
                manifest.AddResource(Config.SpraySoundResource);
        });
        RegisterListener<Listeners.OnMapEnd>(ForgetMap);
        RegisterListener<Listeners.OnMapStart>(_ => ForgetMap());
        RegisterListener<Listeners.OnClientAuthorized>((slot, steamId) => ValidateSelection(steamId.SteamId64));
        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            int generation = _mapGeneration;
            Server.NextWorldUpdate(() =>
            {
                if (!_loaded || generation != _mapGeneration) return;
                if (_keepAcrossRounds.Value) RestoreSprays(force: true);
                else Clear();
            });
            return HookResult.Continue;
        }, HookMode.Post);
        // Also repair engine removals occurring after the round-start callback.
        _repairTimer = AddTimer(1, () => { if (_loaded) RestoreSprays(force: false); }, TimerFlags.REPEAT);
        ValidateOnlineSelections();
        Logger.LogInformation("MG Sprays loaded. Change map after loading to precache materials.");
    }

    private void ValidateOnlineSelections()
    {
        foreach (var player in Utilities.GetPlayers())
            if (player.IsValid && !player.IsBot && player.SteamID != 0) ValidateSelection(player.SteamID);
    }

    private void ValidateSelection(ulong steamId)
    {
        if (steamId == 0) return;
        string? saved = _settings.Get(steamId).SprayName;
        if (saved == null || Config.Sprays.Any(s => s.Id.Equals(saved, StringComparison.OrdinalIgnoreCase))) return;
        _ = ResetMissingSelection(steamId, saved);
    }

    private async Task ResetMissingSelection(ulong steamId, string missingId)
    {
        try { await _settings.ResetSprayIfMatchesAsync(steamId, missingId).ConfigureAwait(false); }
        catch (Exception e)
        {
            // Actual use still falls back to a registered random spray, even when
            // a locked/unavailable database temporarily prevents this repair.
            Logger.LogWarning(e, "Could not reset removed spray {Id} for {SteamId}", missingId, steamId);
        }
    }

    private static void Reply(CCSPlayerController player, string message)
    {
        const string prefix = "[Sprays]";
        string body = message.StartsWith(prefix, StringComparison.Ordinal)
            ? message[prefix.Length..].TrimStart() : message;
        player.PrintToChat($" {ChatColors.Green}{prefix}{ChatColors.Default} {body}");
    }

    private void RegisterCvars()
    {
        RegisterCvar(_enabled, CvarRules.ParseBool, "0/1", () => { if (!_enabled.Value) Clear(); });
        RegisterCvar(_lifetime, text => CvarRules.ParseFloat(text, 1, 3600), "1..3600", RescheduleAll);
        RegisterCvar(_maxDistance, text => CvarRules.ParseFloat(text, 1, 1024), "1..1024");
        RegisterCvar(_depth, text => CvarRules.ParseFloat(text, 1, 16), "1..16");
        RegisterCvar(_maxActive, text => CvarRules.ParseInt(text, 1, 256), "1..256");
        // Lowering either limit does not prune existing decals.
        RegisterCvar(_maxPerPlayer, text => CvarRules.ParseInt(text, 1, 256), "1..256");
        RegisterCvar(_cooldown, text => CvarRules.ParseFloat(text, 0, 3600), "0..3600");
        RegisterCvar(_pitchOffset, text => CvarRules.ParseFloat(text, -360, 360), "-360..360");
        RegisterCvar(_yawOffset, text => CvarRules.ParseFloat(text, -360, 360), "-360..360");
        RegisterCvar(_rollOffset, text => CvarRules.ParseFloat(text, -360, 360), "-360..360");
        RegisterCvar(_debug, CvarRules.ParseBool, "0/1");
        RegisterCvar(_chatEnabled, CvarRules.ParseBool, "0/1");
        RegisterCvar(_soundEnabled, CvarRules.ParseBool, "0/1");
        RegisterCvar(_keepAcrossRounds, CvarRules.ParseBool, "0/1");
    }

    private void RegisterCvar<T>(FakeConVar<T> cvar, Func<string, (bool Valid, T Value)> parse,
        string range, Action? changed = null) where T : struct, IComparable<T>
    {
        // FakeConVar's automatic command permits server callers only. Our command
        // checks root explicitly, while also allowing server console / RCON / cfg.
        if (_runtimeValues.TryGetValue(cvar.Name, out var saved))
        {
            var restored = parse(saved);
            if (restored.Valid) cvar.Value = restored.Value;
        }
        static string Format(T value) => value is bool flag ? (flag ? "1" : "0")
            : Convert.ToString(value, CultureInfo.InvariantCulture)!;
        _runtimeValues[cvar.Name] = Format(cvar.Value);
        AddCommand(cvar.Name, $"{cvar.Description} ({range}; root only)", (player, command) =>
        {
            if (player != null && (!player.IsValid || !AdminManager.PlayerHasPermissions(player, "@css/root"))) return;
            void Respond(string message)
            {
                if (player != null) Reply(player, message);
                else command.ReplyToCommand($"[Sprays] {message}");
            }
            if (command.ArgCount == 1)
            {
                Respond($"{cvar.Name} = {Format(cvar.Value)}");
                return;
            }
            if (command.ArgCount != 2)
            {
                Respond(Localizer.ForPlayer(player, "sprays.cvar_usage", cvar.Name, range));
                return;
            }
            var result = parse(command.GetArg(1));
            if (!result.Valid)
            {
                Respond(Localizer.ForPlayer(player, "sprays.cvar_usage", cvar.Name, range));
                return;
            }
            bool differs = !EqualityComparer<T>.Default.Equals(cvar.Value, result.Value);
            cvar.Value = result.Value;
            _runtimeValues[cvar.Name] = Format(cvar.Value);
            if (differs) changed?.Invoke();
            if (ReferenceEquals(cvar, _enabled))
                Respond(Localizer.ForPlayer(player, _enabled.Value ? "sprays.enabled" : "sprays.disabled"));
            else Respond(Localizer.ForPlayer(player, "sprays.cvar_set", cvar.Name, Format(cvar.Value)));
        });
    }

    private void RescheduleAll()
    {
        double now = Server.TickedTime;
        foreach (var spray in _active.ToArray())
        {
            ScheduleExpiry(spray, now);
        }
    }

    private void ScheduleExpiry(ActiveSpray spray, double now)
    {
        spray.Expiry?.Kill();
        spray.Expiry = null;
        double remaining = CvarRules.Remaining(spray.Visual.Snapshot.PlacedAt, now, _lifetime.Value);
        if (remaining <= 0) { Remove(spray); return; }
        spray.Expiry = AddTimer((float)remaining, () =>
        {
            spray.Expiry = null;
            Remove(spray);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static CEnvDecal SpawnDecal(SpraySnapshot saved)
    {
        var decal = Utilities.CreateEntityByName<CEnvDecal>("env_decal");
        if (decal == null || !decal.IsValid) throw new InvalidOperationException("env_decal creation failed");
        try
        {
            using var kv = new CEntityKeyValues();
            kv.SetString("material", saved.Material);
            kv.SetFloat("width", saved.Width); kv.SetFloat("height", saved.Height); kv.SetFloat("depth", saved.Depth);
            kv.SetBool("projectonworld", true); kv.SetBool("projectoncharacters", false); kv.SetBool("projectonwater", false);
            kv.SetVector("origin", saved.X, saved.Y, saved.Z);
            kv.SetAngle("angles", new QAngle(saved.Pitch, saved.Yaw, saved.Roll));
            decal.DispatchSpawn(kv);
            return decal;
        }
        catch { if (decal.IsValid) decal.Remove(); throw; }
    }

    private void RestoreSprays(bool force)
    {
        if (!_enabled.Value) return;
        double now = Server.TickedTime;
        foreach (var spray in _active.ToArray())
        {
            try
            {
                var refreshed = spray.Visual.Refresh(now, _lifetime.Value, force,
                    d => d.IsValid, d => d.Remove(), SpawnDecal);
                if (refreshed == VisualRefresh.Expired) Remove(spray);
            }
            catch (Exception e) { Logger.LogWarning(e, "Could not restore spray; will retry until expiry"); }
        }
    }
    private async Task PersistPreference(CCSPlayerController player, ulong steamId, Task write,
        string successKey, string failureKey, params object[] successArgs)
    {
        string key = successKey;
        object[] args = successArgs;
        try { await write.ConfigureAwait(false); }
        catch (Exception e)
        {
            Logger.LogError(e, "Could not persist spray preference for {SteamId}", steamId);
            key = failureKey;
            args = [];
        }
        if (!_loaded) return;
        // Persistence runs off-thread; all player/native access returns to the game thread.
        Server.NextWorldUpdate(() =>
        {
            if (_loaded && player.IsValid && player.SteamID == steamId)
                Reply(player, Localizer.ForPlayer(player, key, args));
        });
    }
    private void SetVolume(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || player.IsBot || player.SteamID == 0) return;
        if (command.ArgCount == 1)
        {
            Reply(player, Localizer.ForPlayer(player, "sprays.volume_current", _settings.Get(player.SteamID).SprayVolume));
            return;
        }
        var result = CvarRules.ParseInt(command.GetArg(1), 0, 100);
        if (command.ArgCount != 2 || !result.Valid)
        {
            Reply(player, Localizer.ForPlayer(player, "sprays.volume_usage"));
            return;
        }
        ulong steamId = player.SteamID;
        _ = PersistPreference(player, steamId, _settings.SetVolumeAsync(steamId, result.Value),
            "sprays.volume_set", "sprays.volume_save_failed", result.Value);
    }
    private void PlaySpraySound(CEnvDecal decal)
    {
        if (!_soundEnabled.Value || string.IsNullOrWhiteSpace(Config.SpraySoundEvent)) return;
        // Every event is sent only to listeners sharing this volume. Never also
        // broadcast it, and omit muted listeners entirely. Source attenuation
        // still places the sound at the decal rather than at the listener.
        var groups = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot)
            .GroupBy(p => _settings.Get(p.SteamID).SprayVolume);
        foreach (var group in groups)
        {
            if (group.Key == 0) continue;
            try
            {
                // Some CS2/CSS versions ignore EmitSound's runtime volume.
                // The Workshop event itself defines the requested amplitude.
                string eventName = CvarRules.SoundEventForVolume(Config.SpraySoundEvent, group.Key);
                uint guid = decal.EmitSound(eventName, new RecipientFilter(group.ToArray()), volume: 1f);
                if (_debug.Value)
                    Logger.LogInformation("SpraySound: event={Event}, percent={Percent}, recipients={Count}, guid={Guid}, resource={Resource}",
                        eventName, group.Key, group.Count(), guid, Config.SpraySoundResource);
            }
            catch (Exception e) { Logger.LogWarning(e, "Spray sound failed for listener volume {Volume}", group.Key); }
        }
    }

    private void Select(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null || !player.IsValid || player.IsBot || player.SteamID == 0) return;
        if (!_enabled.Value) return;
        if (command.ArgCount < 2)
        {
            Reply(player, Config.Sprays.Count == 0 ? Localizer.ForPlayer(player, "sprays.empty")
                : string.Join(" / ", Config.Sprays.Select(s => s.Id)));
            return;
        }
        if (command.GetArg(1).Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            ulong steamId = player.SteamID;
            _ = PersistPreference(player, steamId, _settings.SetSprayAsync(steamId, null),
                "sprays.random", "sprays.save_failed");
            return;
        }
        var spray = Config.Sprays.FirstOrDefault(s => (s.Id.Equals(command.GetArg(1), StringComparison.OrdinalIgnoreCase) || s.Name.Equals(command.GetArg(1), StringComparison.OrdinalIgnoreCase)));
        if (spray == null) { Reply(player, Localizer.ForPlayer(player, "sprays.unknown")); return; }
        ulong selectedSteamId = player.SteamID;
        _ = PersistPreference(player, selectedSteamId, _settings.SetSprayAsync(selectedSteamId, spray.Id),
            "sprays.selected", "sprays.save_failed", spray.Name);
    }
    private void Spray(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount > 1) { Select(player, command); return; }
        if (!_enabled.Value) return;
        if (player == null || !player.IsValid || player.IsBot || !player.PawnIsAlive) return;
        if (Config.Sprays.Count == 0) return;
        if (_lastUsed.TryGetValue(player.SteamID, out double lastUsed))
        {
            int seconds = CvarRules.CooldownSecondsLeft(lastUsed, Server.TickedTime, _cooldown.Value);
            if (seconds > 0)
            {
                Reply(player, Localizer.ForPlayer(player, "sprays.cooldown", seconds));
                return;
            }
        }
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) return;
        string? id = _settings.Get(player.SteamID).SprayName;
        var spray = Config.Sprays.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Config.Sprays[Random.Shared.Next(Config.Sprays.Count)];
        CEnvDecal? decal = null;
        ActiveSpray? created = null;
        try
        {
            RestoreSprays(force: false);
            _owned.TryGetValue(player.SteamID, out var owned);
            int ownedCount = owned?.Count ?? 0;
            var placement = CvarRules.PlanPlacement(ownedCount, _maxPerPlayer.Value, _active.Count, _maxActive.Value);
            // Never evict another player's spray to make space.
            if (!placement.Allowed)
            {  return; }
            // Lists are kept in placement order. Replace exactly one, even if a
            // lowered limit left this player temporarily above the new limit.
            ActiveSpray? previous = placement.ReplaceOldest ? owned![0] : null;
            var origin = pawn.AbsOrigin;
            var offset = pawn.ViewOffset;
            var eye = new Vector(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
            var hit = Trace.TraceShape(eye, pawn.EyeAngles, pawn);
            var entity = hit.HitEntity();
            bool validEntity = entity.Handle != nint.Zero && entity.IsValid;
            string target = validEntity ? entity.DesignerName : "<no entity>";
            if (_debug.Value)
                Logger.LogInformation("SprayTrace: hit={Hit}, solid={Solid}, fraction={Fraction}, target={Target}, handle={Handle}, eye={EX},{EY},{EZ}, end={X},{Y},{Z}, normal={NX},{NY},{NZ}",
                    hit.DidHit(), hit.IsAllSolid, hit.Fraction, target, entity.Handle,
                    eye.X, eye.Y, eye.Z, hit.EndPos.X, hit.EndPos.Y, hit.EndPos.Z,
                    hit.Normal.X, hit.Normal.Y, hit.Normal.Z);
            if (!hit.DidHit())
            {  return; }
            if (hit.IsAllSolid)
            {  return; }
            // World physics can report a hit without an entity pointer. Do not equate
            // that with a miss. Named moving entities are not supported.
            if (validEntity && target != "worldspawn" && target != "worldent" && target != "prop_static")
            {  return; }
            var pos = hit.EndPos;
            float dx = pos.X - eye.X, dy = pos.Y - eye.Y, dz = pos.Z - eye.Z;
            if (dx * dx + dy * dy + dz * dz > _maxDistance.Value * _maxDistance.Value)
            {  return; }
            var normal = hit.Normal;
            float length = MathF.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            if (!float.IsFinite(length) || length < 0.5f) {  return; }
            float nx = normal.X / length, ny = normal.Y / length, nz = normal.Z / length;
            // env_decal projects along local Z, not forward X. With roll zero,
            // local up = (sin pitch cos yaw, sin pitch sin yaw, cos pitch).
            // A horizontal floor therefore uses pitch 0; a vertical wall uses 90.
            float tilt = MathF.Acos(Math.Clamp(nz, -1, 1)) * 180 / MathF.PI;
            float azimuth = MathF.Abs(nx) + MathF.Abs(ny) < 0.0001f ? 0 : MathF.Atan2(ny, nx) * 180 / MathF.PI;
            // Rotate within the decal plane, preserving local +Z = surface normal.
            // Local +Y (image top) now follows world up projected onto the surface.
            // Merely adding Euler roll to the old angles would tilt the projection.
            bool horizontal = MathF.Abs(nz) > 0.9999f;
            float pitch = (horizontal ? tilt : 0) + _pitchOffset.Value;
            float yaw = (horizontal ? azimuth : azimuth + 90) + _yawOffset.Value;
            float roll = (horizontal ? 0 : tilt) + _rollOffset.Value;
            var saved = new SpraySnapshot(player.SteamID, spray.Material, spray.Width, spray.Height, _depth.Value,
                pos.X + nx * 0.5f, pos.Y + ny * 0.5f, pos.Z + nz * 0.5f, pitch, yaw, roll, Server.TickedTime);
            created = new ActiveSpray(saved);
            created.Visual.Refresh(Server.TickedTime, _lifetime.Value, false, d => d.IsValid, d => d.Remove(), SpawnDecal);
            decal = created.Visual.Entity!;
            ScheduleExpiry(created, Server.TickedTime);
            if (previous != null) Remove(previous);
            _active.Add(created);
            if (!_owned.TryGetValue(player.SteamID, out owned))
                _owned[player.SteamID] = owned = new();
            owned.Add(created);
            _lastUsed[player.SteamID] = Server.TickedTime;
            if (_chatEnabled.Value) Reply(player, Localizer.ForPlayer(player, "sprays.used"));
            PlaySpraySound(decal);
            if (_debug.Value) Logger.LogInformation("Spray {Id}: entity={Index}, material={Material}, pos={X},{Y},{Z}, normal={NX},{NY},{NZ}, angles={P},{Yaw},{R}",
                spray.Id, decal.Index, spray.Material, pos.X, pos.Y, pos.Z, nx, ny, nz, pitch, yaw, roll);
        }
        catch (Exception e)
        {
            if (created != null) Remove(created);
            Logger.LogError(e, "Spray creation failed");

        }
    }

    private void Remove(ActiveSpray spray)
    {
        spray.Expiry?.Kill();
        spray.Expiry = null;
        spray.Visual.Remove(d => d.Remove(), d => d.IsValid);
        _active.Remove(spray);
        ulong owner = spray.Visual.Snapshot.Owner;
        if (_owned.TryGetValue(owner, out var owned))
        {
            owned.Remove(spray);
            if (owned.Count == 0) _owned.Remove(owner);
        }
    }
    private void ForgetMap()
    {
        _mapGeneration++;
        foreach (var spray in _active) { spray.Expiry?.Kill(); spray.Expiry = null; }
        _owned.Clear(); _active.Clear(); _lastUsed.Clear();
    }
    private void Clear() { foreach (var spray in _active.ToArray()) Remove(spray); }
    public override void Unload(bool hotReload)
    {
        _loaded = false;
        _repairTimer?.Kill();
        Clear();
        _settings?.Dispose();
    }
}

