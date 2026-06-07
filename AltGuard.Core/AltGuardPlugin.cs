using System;
using AltGuard.Configuration;
using AltGuard.Database;
using AltGuard.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;

namespace AltGuard;

/// <summary>
/// AltGuard — detects and acts on ban-evasion alt accounts by shared IP.
///
/// Consumes AdminCommands (ban check + issue) and reads PlayerAnalytics' pa_connections table
/// (own SqlSugar connection) for the reverse IP → SteamID lookup. Enforcement is async off the
/// game thread; bans are applied on the main thread via AdminCommands, whose BanHandler then keeps
/// the alt out at the connection gate.
///
/// Lifecycle: cross-plugin interfaces (AdminCommands/AdminManager) resolve in OnAllModulesLoaded.
/// </summary>
public sealed class AltGuardPlugin : IModSharpModule
{
    public string DisplayName   => "AltGuard";
    public string DisplayAuthor => "yappershq";

    private readonly ILogger<AltGuardPlugin> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly AltGuardConfig           _config;
    private readonly AltGuardDatabase         _db;
    private readonly AltDetectionModule       _detection;

    public AltGuardPlugin(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        var loggerFactory = sharedSystem.GetLoggerFactory();
        _logger = loggerFactory.CreateLogger<AltGuardPlugin>();

        _bridge    = new InterfaceBridge(sharpPath, sharedSystem);
        _config    = AltGuardConfig.Load(sharpPath, loggerFactory.CreateLogger<AltGuardConfig>());
        _db        = new AltGuardDatabase(loggerFactory.CreateLogger<AltGuardDatabase>());
        _detection = new AltDetectionModule(_bridge, _config, _db, loggerFactory.CreateLogger<AltDetectionModule>());
    }

    public bool Init() => true;

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        _bridge.ResolveModules();

        if (_config.Enabled)
        {
            // No DB creds of our own — reuse an existing server config (PlayerAnalytics' by default).
            var dbConfig = DatabaseConfig.LoadShared(_bridge.SharpPath, _config.AnalyticsDatabaseConfig, _logger);
            if (dbConfig is not null && _db.Connect(dbConfig))
                _db.EnsureIpIndex();
        }

        // Shared bypass list (read by both AltGuard + AntiVpnGuard).
        _detection.Configure(Configuration.SharedBypass.Load(_bridge.SharpPath, _config.SharedBypassConfig, _logger));

        _detection.Start();

        _logger.LogInformation("[AltGuard] Loaded (AdminCommands={Admin}, AdminManager={Mgr}, DB={Db})",
            _bridge.AdminService is not null, _bridge.AdminManager is not null, _db.IsConnected);
    }

    public void Shutdown()
    {
        _detection.Stop();
        _db.Dispose();
    }
}
