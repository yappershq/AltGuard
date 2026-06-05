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
}

public sealed class AltGuardConfig
{
    /// <summary>Master switch. When false the connect listener does nothing.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>notify | ban_same | ban_fixed. Defaults to notify so a bad rollout can't mass-ban.</summary>
    [JsonPropertyName("action")] public string ActionRaw { get; set; } = "notify";

    /// <summary>Number of distinct BANNED accounts sharing the IP required before acting.</summary>
    [JsonPropertyName("threshold")] public int Threshold { get; set; } = 1;

    /// <summary>Ban length in minutes for the ban_fixed action. 0 = permanent.</summary>
    [JsonPropertyName("banDurationMinutes")] public int BanDurationMinutes { get; set; } = 0;

    /// <summary>Skip players who are registered admins (avoid banning staff on shared IPs).</summary>
    [JsonPropertyName("adminBypass")] public bool AdminBypass { get; set; } = true;

    /// <summary>SteamID64s exempt from detection entirely (trusted shared-IP households, etc).</summary>
    [JsonPropertyName("whitelist")] public List<string> Whitelist { get; set; } = [];

    /// <summary>Skip re-querying an IP that was already seen clean within this many seconds.</summary>
    [JsonPropertyName("ipCacheSeconds")] public int IpCacheSeconds { get; set; } = 300;

    /// <summary>Reason string used on the issued ban / admin notification.</summary>
    [JsonPropertyName("banReason")] public string BanReason { get; set; } = "Ban evasion (alt of a banned account)";

    [JsonPropertyName("database")] public DatabaseConfig Database { get; set; } = new();

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
