# AltGuard

Ban-evasion / alt-account detection for CS2 (ModSharp). A native port of the concept behind the
SourceMod plugin **SourceSleuth**: when a player connects, AltGuard checks whether their IP has been
used by any **currently-banned** account and, if so, notifies admins or bans the alt.

## How it works

1. On `OnClientPostAdminCheck` (post-auth, IP known) AltGuard takes the connecting SteamID + IP.
2. Off the game thread it looks up every other SteamID seen from that IP in PlayerAnalytics'
   `pa_connections` table (its own read-only SqlSugar connection — no hard dependency on the
   PlayerAnalytics plugin at runtime).
3. For each sibling SteamID it asks **AdminCommands** (`IAdminOperationStorageService.HasActiveAsync`)
   whether they're actively banned.
4. If the number of banned siblings ≥ `threshold`, it acts (back on the main thread):
   - `notify` → message online admins + log
   - `ban_same` → ban the alt for the matched ban's remaining duration
   - `ban_fixed` → ban for `banDurationMinutes`
5. Bans are issued through AdminCommands, whose own `BanHandler` then rejects the alt at the
   connection gate on every future join.

Nothing blocks the game thread; only the final action runs on it.

## Requirements

- **AdminCommands** module (with a persistent storage provider, e.g. `AdminCommands.SQLStorage`).
- **PlayerAnalytics** logging connections into `pa_connections` (AltGuard reads the same DB).

## Config (`sharp/configs/altguard.json`)

See [`.assets/configs/altguard.json.example`](.assets/configs/altguard.json.example). Point
`database` at the PlayerAnalytics DB. **Start with `"action": "notify"`** to validate detection
before enabling bans — shared IPs (households, CGNAT) can produce false positives, mitigated by
`threshold`, `adminBypass`, and the SteamID `whitelist`.

## Caveats

- Exact-IP match only (no subnet / VPN awareness), same as SourceSleuth.
- Detection runs post-connect (the connect hook is synchronous, so a DB lookup can't gate it) — an
  alt connects once before being acted on; after a ban, AdminCommands keeps them out.
- AltGuard ensures an index on `pa_connections(IpAddress)` at startup for fast reverse lookups.

## Build / deploy

```
dotnet build AltGuard.slnx -c Release
modsharp-deploy /path/to/AltGuard <server-profile>
```
