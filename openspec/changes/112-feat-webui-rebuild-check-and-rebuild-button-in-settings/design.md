## Context

mohist runs in two modes: **source mode** (cloned repo, `detectInstallMode().workingDir` defined) and **npm global mode**. In source mode, the CLI `mo server update` command rebuilds (`npm run build` in cli + web) and restarts (`systemctl --user restart mohist`), but there is no WebUI equivalent. The server already has `getVersionInfo()` in `packages/cli/src/version.ts` that resolves `gitHash` at startup via `git rev-parse --short HEAD` (cached for the process lifetime). The SettingsPage currently has "Providers" and "General" tabs; the General tab renders `GeneralSettingsSection` with config fields.

The existing `updateServer()` in `server-systemd.ts:348` contains the exact build + restart sequence to reuse.

## Goals / Non-Goals

**Goals:**
- Expose source-vs-build staleness in the existing `GET /api/status` response (additive fields)
- Provide `POST /api/settings/system/rebuild` for one-click rebuild in source mode
- Add a System/About section in Settings page with staleness indicator and rebuild button
- Handle reconnect UX after rebuild triggers server restart

**Non-Goals:**
- No remote git comparison (no `git fetch`, no GitHub API) — source mode is a dev environment, only compare local HEAD vs build hash
- No build progress streaming — fire-and-forget with client-side reconnect polling
- No rebuild queue or dedup — if user clicks twice, second click while first is running is a no-op on the server side (systemd restart is idempotent)

## Decisions

### D1: Extend `GET /api/status` instead of creating a separate system info endpoint

The `GET /api/status` route in `status.ts` already returns `version` and `gitHash`. Adding `sourceHead` and `upToDate` there is simpler than creating a new `/api/settings/system/info` route. The frontend already has a pattern of calling `/api/status`.

**Alternatives considered:** Separate `/api/settings/system/info` endpoint — rejected because it fragments version info across two endpoints with no clear benefit.

### D2: Rebuild runs via `child_process.spawn` in the server process

The rebuild endpoint spawns build commands as a detached child process, then calls `systemctl --user restart mohist`. This is identical to `updateServer()` in `server-systemd.ts`. The function will be extracted to a shared utility (or imported directly from server-systemd) so the API route can call it programmatically.

**Alternatives considered:** 
- Shell script executed via `execSync` — rejected because blocking the event loop during build (potentially minutes) is unacceptable
- Separate rebuild service/queue — over-engineering for a single fire-and-forget action

### D3: Frontend reconnect via polling `GET /api/health`

After rebuild, the server restarts and connections drop. The frontend polls `/api/health` at 5-second intervals (max 60 seconds). On success, it refreshes system info. This matches the simple, reliable approach — no WebSocket needed.

### D4: Add "System" tab to SettingsPage

Current tabs: Providers, General. Add a third "System" tab containing an About section with version info + rebuild button. This keeps the rebuild UX in Settings (where users expect system management) without cluttering the General config tab.

**Alternatives considered:** Add About section to bottom of General tab — rejected because General already has config fields and would grow unwieldy; System/About is a distinct concern.

### D5: Staleness check is per-request, not cached

`sourceHead` is resolved fresh on each `GET /api/status` call via `git rev-parse HEAD` in the `detectInstallMode().workingDir` directory. This is a cheap local git operation (< 10ms) and avoids stale cache issues if the user makes commits.

## Risks / Trade-offs

- **[Build fails after API responds]** → Background process logs failure; server stays running with old build. User sees stale version next time they check. No automatic notification of failure — acceptable for MVP since the user will see the version hasn't changed.
- **[git rev-parse overhead]** → < 10ms per request, negligible. If it ever matters, can be cached with a short TTL (e.g. 30s), but YAGNI for now.
- **[Non-source mode users see nothing]** → By design. `sourceHead` is `null`, `upToDate` is `true`. The System tab shows version info but no rebuild button. No confusing UX for npm users.

## Migration Plan

1. Backend: Add `sourceHead`/`upToDate` to status route response, add rebuild route
2. Frontend: Add System tab with About section and rebuild button
3. Deploy: Standard `mo server update` — additive API change, no migration needed
4. Rollback: Remove System tab; API fields are additive and backward-compatible

## Open Questions

None — all key decisions resolved during explore phase.
