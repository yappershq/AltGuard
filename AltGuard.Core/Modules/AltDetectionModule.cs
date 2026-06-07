using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AltGuard.Configuration;
using AltGuard.Database;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminCommands.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace AltGuard.Modules;

/// <summary>
/// Ban-evasion / alt detection. On admin-check (post-auth, IP known) we async-look-up every other
/// SteamID seen from the connecting IP, ask AdminCommands which of them are actively banned, and if
/// the count meets the threshold we notify admins or ban the alt. Mirrors SourceMod's SourceSleuth.
/// All DB / ban-store work runs off the game thread; only the final action touches the main thread.
/// </summary>
internal sealed class AltDetectionModule : IClientListener
{
    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge              _bridge;
    private readonly AltGuardConfig               _config;
    private readonly AltGuardDatabase             _db;
    private readonly ILogger<AltDetectionModule>  _logger;

    // IP -> time until which it's considered clean (skip re-querying on reconnect spam).
    private readonly ConcurrentDictionary<string, DateTime> _cleanUntil = new();

    private bool                       _installed;
    private Database.GuardBypassStore? _bypass;

    /// <summary>Supply the shared bypass store (DB-backed, shared with AntiVpnGuard). Call before Start.</summary>
    public void Configure(Database.GuardBypassStore bypass) => _bypass = bypass;

    public AltDetectionModule(InterfaceBridge bridge, AltGuardConfig config, AltGuardDatabase db,
                              ILogger<AltDetectionModule> logger)
    {
        _bridge = bridge;
        _config = config;
        _db     = db;
        _logger = logger;
    }

    public void Start()
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("[AltGuard] Disabled by config");
            return;
        }
        if (!_db.IsConnected)
        {
            _logger.LogWarning("[AltGuard] No DB connection — detection inactive");
            return;
        }
        if (_bridge.BanStorage is null)
        {
            _logger.LogWarning("[AltGuard] AdminCommands ban storage unavailable — detection inactive");
            return;
        }

        _bridge.ClientManager.InstallClientListener(this);
        _installed = true;
        _logger.LogInformation("[AltGuard] Active — action={Action}, threshold={Threshold}", _config.Action, _config.Threshold);
    }

    public void Stop()
    {
        if (_installed)
            _bridge.ClientManager.RemoveClientListener(this);
        _installed = false;
    }

    public void OnClientPostAdminCheck(IGameClient client)
    {
        if (client.IsFakeClient || client.IsHltv)
            return;

        var steamId  = client.SteamId;
        var steamStr = ((ulong) steamId).ToString();

        if (_config.Whitelist.Contains(steamStr) || (_bypass?.Contains(steamStr) ?? false))
            return;

        if (_config.AdminBypass && _bridge.AdminManager?.GetAdmin(steamId) is not null)
            return;

        var ip = client.GetAddress(false);
        if (string.IsNullOrEmpty(ip))
            return;

        if (_cleanUntil.TryGetValue(ip, out var until) && until > DateTime.UtcNow)
            return;

        var name = client.Name ?? "?";
        _ = Task.Run(() => DetectAsync(steamId, steamStr, ip, name));
    }

    private async Task DetectAsync(SteamID connecting, string connectingStr, string ip, string name)
    {
        try
        {
            if (_bridge.BanStorage is not { } storage)
                return;

            var siblings = await _db.GetSteamIdsByIpAsync(ip, connectingStr).ConfigureAwait(false);
            if (siblings.Count == 0)
            {
                MarkClean(ip);
                return;
            }

            var bannedRecords = new List<AdminOperationRecord>();
            foreach (var s in siblings)
            {
                if (!ulong.TryParse(s, out var sid))
                    continue;

                SteamID siblingSteam = sid;
                if (!await storage.HasActiveAsync(siblingSteam, AdminOperationType.Ban).ConfigureAwait(false))
                    continue;

                if (await storage.GetAsync(siblingSteam, AdminOperationType.Ban).ConfigureAwait(false) is { } rec)
                    bannedRecords.Add(rec);
            }

            if (bannedRecords.Count < _config.Threshold)
            {
                MarkClean(ip);
                return;
            }

            // Match against the harshest ban (permanent, else longest remaining) for ban_same duration.
            var matched = bannedRecords
                .OrderByDescending(r => r.IsPermanent ? DateTime.MaxValue : r.ExpiresAt ?? DateTime.MaxValue)
                .First();

            var count = bannedRecords.Count;
            _bridge.ModSharp.InvokeFrameAction(() => Act(connecting, connectingStr, ip, name, count, matched));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[AltGuard] Detection failed for {Steam} @ {Ip}", connectingStr, ip);
        }
    }

    private void Act(SteamID connecting, string steamStr, string ip, string name, int matchCount, AdminOperationRecord matched)
    {
        // Re-resolve on the main thread; the player may have left while we queried.
        var client = _bridge.ClientManager.GetGameClient(connecting);
        if (client is null || !client.IsValid || client.IsFakeClient)
            return;

        _logger.LogWarning("[AltGuard] {Name} ({Steam}) @ {Ip} shares IP with {Count} banned account(s); action={Action}",
            name, steamStr, ip, matchCount, _config.Action);

        switch (_config.Action)
        {
            case AltAction.Notify:
                NotifyAdmins(name, steamStr, ip, matchCount);
                break;

            case AltAction.BanSame:
                IssueBan(client, RemainingOf(matched));
                break;

            case AltAction.BanFixed:
                var dur = _config.BanDurationMinutes <= 0 ? (TimeSpan?) null : TimeSpan.FromMinutes(_config.BanDurationMinutes);
                IssueBan(client, dur);
                break;
        }
    }

    private void IssueBan(IGameClient client, TimeSpan? duration)
    {
        if (_bridge.AdminService is not { } admin)
        {
            _logger.LogError("[AltGuard] AdminService unavailable — cannot ban {Steam}", (ulong) client.SteamId);
            return;
        }

        // null admin = console/system ban; kicks the online target and persists via AdminCommands,
        // whose own BanHandler then blocks the alt's future connects at the connection gate.
        admin.Apply(null, client, AdminOperationType.Ban, duration, _config.BanReason);
    }

    private void NotifyAdmins(string name, string steamStr, string ip, int matchCount)
    {
        var msg = $" [AltGuard] Possible alt: {name} <{steamStr}> @ {ip} — shares IP with {matchCount} banned account(s).";
        foreach (var c in _bridge.ClientManager.GetGameClients(inGame: true))
        {
            if (c.IsFakeClient || c.IsHltv)
                continue;
            if (_bridge.AdminManager?.GetAdmin(c.SteamId) is null)
                continue;
            c.Print(HudPrintChannel.Chat, msg);
        }
    }

    private static TimeSpan? RemainingOf(AdminOperationRecord r)
    {
        if (r.IsPermanent || !r.ExpiresAt.HasValue)
            return null; // permanent
        var remaining = r.ExpiresAt.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMinutes(1);
    }

    private void MarkClean(string ip)
    {
        if (_config.IpCacheSeconds > 0)
            _cleanUntil[ip] = DateTime.UtcNow.AddSeconds(_config.IpCacheSeconds);
    }
}
