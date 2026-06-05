using System;
using SqlSugar;

namespace AltGuard.Database;

/// <summary>
/// Read-only view of PlayerAnalytics' <c>pa_connections</c> table — only the columns AltGuard needs.
/// Column names are PascalCase because PlayerAnalytics' SqlSugar config maps by property name with
/// no snake_case convention (see its SugarExtensions). We never write this table.
/// </summary>
[SugarTable("pa_connections")]
internal sealed class ConnectionRow
{
    [SugarColumn(ColumnName = "SteamId")]
    public string SteamId { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "IpAddress")]
    public string IpAddress { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "ConnectedAt")]
    public DateTime ConnectedAt { get; set; }
}
