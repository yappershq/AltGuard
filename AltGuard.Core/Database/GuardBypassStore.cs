using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace AltGuard.Database;

/// <summary>
/// SteamID row in the shared <c>guard_bypass</c> table (lives in AltGuard's own DB, website-managed).
/// </summary>
[SugarTable("guard_bypass")]
internal sealed class GuardBypassRow
{
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
/// DB-backed bypass list, refreshed into an in-memory set on a timer. Reuses the plugin's existing
/// <see cref="AltGuardDatabase"/> connection (NO separate pool — the shared MySQL box is
/// connection-constrained). The on-disk JSON file is merged as an offline fallback.
/// Lookups are lock-free (volatile reference swap).
/// </summary>
internal sealed class GuardBypassStore : IDisposable
{
    private readonly ILogger         _logger;
    private readonly HashSet<string> _fileFallback;
    private readonly AltGuardDatabase _db;

    private volatile HashSet<string> _ids = [];
    private Timer?                   _timer;
    private bool                     _disposed;

    public GuardBypassStore(AltGuardDatabase db, HashSet<string> fileFallback, ILogger logger)
    {
        _db           = db;
        _fileFallback = fileFallback ?? [];
        _logger       = logger;
    }

    /// <summary>True if the SteamID (string form) is bypassed — DB set OR the file fallback.</summary>
    public bool Contains(string steamId) => _ids.Contains(steamId) || _fileFallback.Contains(steamId);

    /// <summary>Load once now, then refresh every refreshSeconds. No-op if DB isn't connected.</summary>
    public void Start(int refreshSeconds)
    {
        if (!_db.IsConnected) return;

        _ = RefreshAsync();

        var period = TimeSpan.FromSeconds(Math.Max(30, refreshSeconds));
        _timer = new Timer(_ => { if (!_disposed) _ = RefreshAsync(); }, null, period, period);
    }

    private async Task RefreshAsync()
    {
        var set = await _db.GetBypassSteamIdsAsync().ConfigureAwait(false);
        _ids = set;
        _logger.LogDebug("[AltGuard] Bypass list refreshed — {Count} SteamIDs", set.Count);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
