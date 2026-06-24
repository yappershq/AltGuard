<div align="center">
  <h1><strong>AltGuard</strong></h1>
  <p>Ban-evasion / alt-account detection for CS2 servers running ModSharp.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/github/stars/yappershq/AltGuard?style=flat&logo=github" alt="Stars">
</p>

---

AltGuard catches players evading bans on a fresh account. When someone connects, it checks whether their IP has been used by any **currently-banned** account and — depending on config — notifies admins or bans the alt. It needs no database credentials of its own: it reads connection history from PlayerAnalytics' DB and issues bans through AdminCommands.

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/AltGuard.Core/` | `<sharp>/modules/AltGuard.Core/` |
| `.build/configs/altguard.json.example` | `<sharp>/configs/altguard.json` |

Restart the server (or change map) to load. `altguard.json` is also auto-written with defaults on first run if absent.

**Requires:**
- **AdminCommands** with a persistent storage provider (e.g. `AdminCommands.SQLStorage`) — used to check active bans and to issue them.
- **PlayerAnalytics** logging connections into its `pa_connections` table — AltGuard reads the same DB for the IP → SteamID lookup.

## ⚙️ Configuration

`configs/altguard.json` (auto-generated on first run):

| Setting | Default | Meaning |
|---------|---------|---------|
| `enabled` | `true` | Master switch. When false, the connect listener does nothing. |
| `action` | `ban_same` | `notify` (message admins + log, never bans) · `ban_same` (ban for the matched ban's remaining duration) · `ban_fixed` (ban for `banDurationMinutes`). |
| `threshold` | `1` | Number of distinct banned accounts sharing the IP required before acting. |
| `banDurationMinutes` | `0` | Ban length in minutes for `ban_fixed`. `0` = permanent. |
| `adminBypass` | `true` | Skip registered admins (avoid banning staff on shared household/CGNAT IPs). |
| `whitelist` | `[]` | SteamID64s exempt from detection entirely. |
| `ipCacheSeconds` | `300` | Skip re-querying an IP already seen clean within this many seconds. |
| `bypassRefreshSeconds` | `300` | How often (seconds) to refresh the bypass list from the DB. |
| `banReason` | `Ban evasion (alt of a banned account)` | Reason string used on the issued ban / notification. |
| `sharedBypassConfig` | `bypass_steamids.json` | Optional `configs/` file (`{ "steamIds": [...] }`) used as an offline fallback bypass list, merged on top of the DB list. |
| `bypassDatabaseConfig` | `""` | Optional separate `configs/` file with a `database` block for the bypass table. Empty = use AltGuard's own DB connection. |
| `analyticsDatabaseConfig` | `playeranalytics.database.jsonc` | Existing server config to pull the analytics DB credentials from, so AltGuard needs no DB creds of its own. |

> **Start with `"action": "notify"`** to validate detection before enabling bans — shared IPs (households, CGNAT) can produce false positives, mitigated by `threshold`, `adminBypass`, and the `whitelist`.

## 🔧 How it works

1. On `OnClientPostAdminCheck` (post-auth, IP known) AltGuard takes the connecting SteamID + IP.
2. Off the game thread it looks up every other SteamID seen from that IP in PlayerAnalytics' `pa_connections` table (its own SqlSugar connection — no runtime dependency on the PlayerAnalytics plugin).
3. For each sibling SteamID it asks AdminCommands (`IAdminOperationStorageService.HasActiveAsync`) whether they're actively banned.
4. If the number of banned siblings ≥ `threshold`, it acts back on the main thread per `action`.
5. Bans are issued through AdminCommands, whose own `BanHandler` then rejects the alt at the connection gate on every future join.

Nothing blocks the game thread; only the final action runs on it. AltGuard ensures an index on `pa_connections(IpAddress)` at startup for fast reverse lookups.

**Caveats:** exact-IP match only (no subnet / VPN awareness). Detection runs post-connect — an alt connects once before being acted on; after the ban, AdminCommands keeps them out.

## 📦 Build

```bash
dotnet build -c Release
```

Outputs `.build/modules/AltGuard.Core/AltGuard.dll` plus the config example under `.build/configs/`.

## 🙏 Credits

Native port of the concept behind the SourceMod plugin **SourceSleuth** by Powerlord.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
