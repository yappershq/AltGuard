using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AltGuard.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AltGuard.Database;

/// <summary>
/// Self-contained SqlSugar access to the analytics <c>pa_connections</c> table. Read-only:
/// reverse IP → SteamID lookup for alt detection, plus an index ensure so that lookup stays fast.
/// All calls are async and run off the game thread.
/// </summary>
internal sealed class AltGuardDatabase : IDisposable
{
    private readonly ILogger<AltGuardDatabase> _logger;
    private SqlSugarScope?                      _db;

    public bool IsConnected => _db is not null;

    public AltGuardDatabase(ILogger<AltGuardDatabase> logger) => _logger = logger;

    public bool Connect(DatabaseConfig cfg)
    {
        try
        {
            var dbType = cfg.Type.ToLowerInvariant() switch
            {
                "mysql"      => DbType.MySql,
                "postgresql" => DbType.PostgreSQL,
                _            => throw new NotSupportedException($"Unsupported DB type '{cfg.Type}' (mysql|postgresql)"),
            };

            var conn = dbType switch
            {
                DbType.MySql      => $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};User={cfg.User};Password={cfg.Password};AllowPublicKeyRetrieval=true;",
                _                 => $"Host={cfg.Host};Port={cfg.Port};Database={cfg.Database};Username={cfg.User};Password={cfg.Password};",
            };

            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = dbType,
                ConnectionString      = conn,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });

            // Probe the connection so a bad config fails loudly at load instead of on first connect.
            _ = _db.Ado.GetInt("SELECT 1");
            _logger.LogInformation("[AltGuard] Connected to analytics DB {Host}/{Db}", cfg.Host, cfg.Database);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[AltGuard] Failed to connect to analytics DB — detection disabled");
            _db = null;
            return false;
        }
    }

    /// <summary>Create an index on pa_connections(IpAddress) if missing — the table ships indexed on SteamId only.</summary>
    public void EnsureIpIndex()
    {
        if (_db is null) return;
        try
        {
            const string table = "pa_connections";
            const string index = "Index_pa_connections_IpAddress";
            if (!_db.DbMaintenance.IsAnyIndex(index))
            {
                _db.DbMaintenance.CreateIndex(table, ["IpAddress"]);
                _logger.LogInformation("[AltGuard] Created index {Index} on {Table}(IpAddress)", index, table);
            }
        }
        catch (Exception e)
        {
            // Non-fatal: detection still works without the index, just slower on big tables.
            _logger.LogWarning(e, "[AltGuard] Could not ensure pa_connections IpAddress index");
        }
    }

    /// <summary>Distinct SteamID64s (as strings) that have connected from <paramref name="ip"/>, excluding the connecting player.</summary>
    public async Task<List<string>> GetSteamIdsByIpAsync(string ip, string excludeSteamId)
    {
        if (_db is null || string.IsNullOrEmpty(ip))
            return [];

        return await _db.Queryable<ConnectionRow>()
            .Where(r => r.IpAddress == ip && r.SteamId != excludeSteamId && r.SteamId != string.Empty)
            .Select(r => r.SteamId)
            .Distinct()
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }
}
