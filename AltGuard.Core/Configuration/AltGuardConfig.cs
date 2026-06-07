using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AltGuard.Configuration;

/// <summary>What to do when a connecting player is found to share an IP with a banned account.</summary>
public enum AltAction
{
    /// <summary>Only message online admins + log. Never bans. Safe default for first rollout.</summary>
    Notify,

    /// <summary>Ban the alt for the same remaining duration as the matched ban (permanent stays permanent).</summary>
    BanSame,

    /// <summary>Ban the alt for a fixed duration (<see cref="AltGuardConfig.BanDurationMinutes"/>; 0 = permanent).</summary>
    BanFixed,
}

public sealed class DatabaseConfig
{
    [JsonPropertyName("type")]     public string Type     { get; set; } = "mysql";
    [JsonPropertyName("host")]     public string Host     { get; set; } = "localhost";
    [JsonPropertyName("port")]     public int    Port     { get; set; } = 3306;
    [JsonPropertyName("database")] public string Database { get; set; } = "player_analytics";
    [JsonPropertyName("user")]     public string User     { get; set; } = "root";
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;

    // Wrapper matching PlayerAnalytics' { "Database": { ... } } config file shape.
    private sealed class AnalyticsDbFile
    {
        [JsonPropertyName("database")] public DatabaseConfig? Database { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,           // analytics uses PascalCase keys (Type/Host/…)
        ReadCommentHandling         = JsonCommentHandling.Skip, // .jsonc
        AllowTrailingCommas         = true,
    };

    /// <summary>
    /// Reuse an existing DB config already on the server (default PlayerAnalytics' database config)
    /// so AltGuard never duplicates credentials. Returns null if the file is missing/unparseable.
    /// </summary>
    public static DatabaseConfig? LoadShared(string sharpPath, string fileName, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", fileName);
        try
        {
            if (!File.Exists(path))
            {
                logger.LogError("[AltGuard] Shared DB config '{Path}' not found — set 'analyticsDatabaseConfig' or create it", path);
                return null;
            }

            var db = JsonSerializer.Deserialize<AnalyticsDbFile>(File.ReadAllText(path), JsonOpts)?.Database;
            if (db is null || string.IsNullOrWhiteSpace(db.Host))
            {
                logger.LogError("[AltGuard] '{Path}' has no usable Database section", path);
                return null;
            }

            logger.LogInformation("[AltGuard] Using DB config from {File}", fileName);
            return db;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[AltGuard] Failed to read shared DB config '{Path}'", path);
            return null;
        }
    }
}

public sealed class AltGuardConfig
{
    /// <summary>Master switch. When false the connect listener does nothing.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>notify | ban_same | ban_fixed. Defaults to ban_same (ban the alt for the matched ban's remaining time).</summary>
    [JsonPropertyName("action")] public string ActionRaw { get; set; } = "ban_same";

    /// <summary>Number of distinct BANNED accounts sharing the IP required before acting.</summary>
    [JsonPropertyName("threshold")] public int Threshold { get; set; } = 1;

    /// <summary>Ban length in minutes for the ban_fixed action. 0 = permanent.</summary>
    [JsonPropertyName("banDurationMinutes")] public int BanDurationMinutes { get; set; } = 0;

    /// <summary>Skip players who are registered admins (avoid banning staff on shared IPs).</summary>
    [JsonPropertyName("adminBypass")] public bool AdminBypass { get; set; } = true;

    /// <summary>SteamID64s exempt from detection entirely (trusted shared-IP households, etc).</summary>
    [JsonPropertyName("whitelist")] public List<string> Whitelist { get; set; } = [];

    /// <summary>
    /// Optional shared bypass file (in configs/) read by BOTH AltGuard and AntiVpnGuard:
    /// { "steamIds": ["7656..."] }. Used as an OFFLINE FALLBACK merged on top of the DB list.
    /// </summary>
    [JsonPropertyName("sharedBypassConfig")] public string SharedBypassConfig { get; set; } = "bypass_steamids.json";

    /// <summary>
    /// Config file (in configs/) holding the shared bypass DB creds as a top-level "database" block.
    /// Hosts the fleet-wide, website-managed guard_bypass table read by both plugins. Empty = file-only.
    /// </summary>
    [JsonPropertyName("bypassDatabaseConfig")] public string BypassDatabaseConfig { get; set; } = "guardbypass.database.json";

    /// <summary>How often (seconds) to refresh the bypass list from the DB. Default 300 (5 min).</summary>
    [JsonPropertyName("bypassRefreshSeconds")] public int BypassRefreshSeconds { get; set; } = 300;

    /// <summary>Skip re-querying an IP that was already seen clean within this many seconds.</summary>
    [JsonPropertyName("ipCacheSeconds")] public int IpCacheSeconds { get; set; } = 300;

    /// <summary>Reason string used on the issued ban / admin notification.</summary>
    [JsonPropertyName("banReason")] public string BanReason { get; set; } = "Ban evasion (alt of a banned account)";

    /// <summary>
    /// Existing server config file (in configs/) to pull the analytics DB credentials from, so
    /// AltGuard needs no DB creds of its own. Defaults to PlayerAnalytics' database config.
    /// </summary>
    [JsonPropertyName("analyticsDatabaseConfig")] public string AnalyticsDatabaseConfig { get; set; } = "playeranalytics.database.jsonc";

    [JsonIgnore]
    public AltAction Action => ActionRaw.Trim().ToLowerInvariant() switch
    {
        "ban_same"  => AltAction.BanSame,
        "ban_fixed" => AltAction.BanFixed,
        _           => AltAction.Notify,
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
    };

    public static AltGuardConfig Load(string sharpPath, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", "altguard.json");
        try
        {
            if (!File.Exists(path))
            {
                var def = new AltGuardConfig();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts));
                logger.LogInformation("[AltGuard] Wrote default config to {Path}", path);
                return def;
            }

            var cfg = JsonSerializer.Deserialize<AltGuardConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                logger.LogError("[AltGuard] altguard.json deserialized to null — using defaults");
                return new AltGuardConfig();
            }
            return cfg;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[AltGuard] Failed to load altguard.json — using defaults");
            return new AltGuardConfig();
        }
    }
}
