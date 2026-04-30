## Why

In source mode, users editing mohist code have no indication whether the running server matches the current source HEAD. They must manually run `mo server update` to rebuild and restart. WebUI should surface this mismatch and provide a one-click rebuild action.

## What Changes

- `GET /api/status` adds `sourceHead` (live `git rev-parse HEAD`) and `upToDate` (boolean, `sourceHead === gitHash`) fields
- New `POST /api/settings/system/rebuild` endpoint triggers background build + restart (source mode only)
- WebUI Settings > System > About area shows build vs source commit comparison with status indicator
- WebUI adds "Rebuild & Restart" button with reconnect UX (rebuilding spinner → restart countdown → auto-reconnect)

## Capabilities

### New Capabilities

- **rebuild-api**: `POST /api/settings/system/rebuild` endpoint that triggers `npm run build` (cli + web) followed by `systemctl restart mohist`, only available in source mode
- **source-staleness-detection**: Comparing build-time git hash against live source HEAD to detect out-of-date builds

### Modified Capabilities

- **http-api**: `GET /api/status` response schema gains `sourceHead` and `upToDate` fields — backward-compatible additive change

## Impact

- **Files**: `status.ts` (API routes), `version.ts` (source HEAD helper), `server-systemd.ts` (reuse build+restart logic), `SystemSettingsSection.tsx` (frontend UI)
- **API**: Additive change to existing endpoint, one new POST endpoint
- **Dependencies**: None
- **Scope**: Only affects source mode installations; npm-installed instances are unaffected
