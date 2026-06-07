using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AltGuard.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AltGuard.Database;

/// <summary>
/// SteamID row in the shared <c>guard_bypass</c> table — shared across the whole server fleet
/// (managed from the website) and read by both AltGuard and AntiVpnGuard.
/// </summary>
[SugarTable("guard_bypass")]
internal sealed class GuardBypassRow
{
    /// <summary>SteamID64 (BIGINT — never ulong, SqlSugar rejects it).</summary>
    [SugarColumn(ColumnName = "steam_id", IsPrimaryKey = true)]
    public long SteamId { get; set; }

    [SugarColumn(ColumnName = "reason", Length = 255, IsNullable = true)]
    public string? Reason { get; set; }

    [SugarColumn(ColumnName = "added_by", Length = 64, IsNullable = true)]
    public string? AddedBy { get; set; }

    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// DB-backed bypass list, shared fleet-wide and managed via the website. Loads all SteamIDs into an
/// in-memory set on connect, then refreshes on a timer. Lookups are lock-free (volatile reference
/// swap). The on-disk JSON file (if any) is merged as an offline fallback.
/// </summary>
internal sealed class GuardBypassStore : IDisposable
{
    private readonly ILogger          _logger;
    private readonly HashSet<string>  _fileFallback;

    private SqlSugarScope?            _db;
    private volatile HashSet<string>  _ids = [];
    private Timer?                    _timer;
    private bool                      _disposed;

    public bool IsConnected => _db is not null;

    public GuardBypassStore(HashSet<string> fileFallback, ILogger logger)
    {
        _fileFallback = fileFallback ?? [];
        _logger       = logger;
    }

    /// <summary>True if the SteamID (string form) is bypassed — DB set OR file fallback.</summary>
    public bool Contains(string steamId) => _ids.Contains(steamId) || _fileFallback.Contains(steamId);

    public bool Connect(DatabaseConfig cfg)
    {
        try
        {
            var dbType = cfg.Type.ToLowerInvariant() switch
            {
                "mysql"      => DbType.MySql,
                "postgresql" => DbType.PostgreSQL,
                _            => throw new NotSupportedException($"Unsupported DB type '{cfg.Type}'"),
            };

            var conn = dbType switch
            {
                DbType.MySql => $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};User={cfg.User};Password={cfg.Password};AllowPublicKeyRetrieval=true;",
                _            => $"Host={cfg.Host};Port={cfg.Port};Database={cfg.Database};Username={cfg.User};Password={cfg.Password};",
            };

            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = dbType,
                ConnectionString      = conn,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });

            _ = _db.Ado.GetInt("SELECT 1");
            _db.CodeFirst.InitTables<GuardBypassRow>();
            _logger.LogInformation("[AltGuard] Bypass DB connected ({Host}/{Db}) — guard_bypass table ensured", cfg.Host, cfg.Database);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AltGuard] Bypass DB connect failed — using file fallback only ({Count} ids)", _fileFallback.Count);
            _db = null;
            return false;
        }
    }

    public void Start(int refreshSeconds)
    {
        if (_db is null) return;

        _ = RefreshAsync();

        var period = TimeSpan.FromSeconds(Math.Max(30, refreshSeconds));
        _timer = new Timer(_ => { if (!_disposed) _ = RefreshAsync(); }, null, period, period);
    }

    private async Task RefreshAsync()
    {
        if (_db is null) return;
        try
        {
            var rows = await _db.Queryable<GuardBypassRow>().Select(r => r.SteamId).ToListAsync().ConfigureAwait(false);
            var set  = new HashSet<string>(rows.Select(id => id.ToString()));
            _ids = set;
            _logger.LogDebug("[AltGuard] Bypass list refreshed — {Count} SteamIDs", set.Count);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[AltGuard] Bypass list refresh failed — keeping previous set ({Count})", _ids.Count);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _db?.Dispose();
        _db = null;
    }
}
