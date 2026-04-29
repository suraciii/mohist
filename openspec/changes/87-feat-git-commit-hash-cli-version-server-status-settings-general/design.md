## Context

mohist currently hardcodes `0.1.0` in `cli/index.ts:17` via commander's `.version('0.1.0')`. There is no git commit hash anywhere in the CLI output, server logs, API responses, or WebUI. The server startup (`server/index.ts`) jumps straight into config loading with no version banner. The `GET /api/health` endpoint returns only `{ status, timestamp }`. The WebUI's `GeneralSettingsSection.tsx` has no version display.

All consumers (CLI, server, API, WebUI) need to read from a single source of truth.

## Goals / Non-Goals

**Goals:**
- Single `getVersionInfo()` function that all surfaces consume
- CLI `mo --version` and `mo server status` show version + git hash
- Server startup log includes version banner as first line
- `GET /api/health` and `GET /api/status` return version fields
- WebUI Settings > General shows version block

**Non-Goals:**
- No build-time code generation (no webpack/rollup plugins, no build scripts)
- No version display in WebUI header/navbar (deliberately excluded as noise)
- No automatic update checking

## Decisions

### D1: Single version module at `packages/cli/src/version.ts`

Create one module with `getVersionInfo()` that reads version from `package.json` and git hash from `git rev-parse --short HEAD`. All consumers import from here.

**Why single module:** Avoids duplicating git logic across CLI, server, and API. The function is synchronous (uses `execSync`) and cheap (one git subprocess call).

**Alternatives considered:**
- Build-time file generation (e.g., `src/generated/version.ts`): Adds build step complexity, breaks `npm run build` flow. Unnecessary since mohist runs from source 99% of the time.
- Environment variable injection: Requires modifying startup scripts. More moving parts.
- `git describe --tags`: Overkill — no tags exist, would need tag discipline.

### D2: Lazy singleton via module-level cache

`getVersionInfo()` executes git once, caches the result in a module-level variable, and returns the cached value on subsequent calls. This avoids repeated subprocess spawns.

```ts
let cached: VersionInfo | null = null;
export function getVersionInfo(): VersionInfo {
  if (cached) return cached;
  // ... compute and cache
  return cached;
}
```

### D3: Server passes version info to status routes via parameter

`createStatusRoutes()` already receives service instances. Add a `versionInfo: VersionInfo` parameter so the route handlers can include it in responses. Same for the `/health` handler. No global state needed.

**Alternatives considered:**
- Import `getVersionInfo()` directly in `api/status.ts`: Works but couples API layer to version module's internals. Passing as parameter is cleaner and testable.

### D4: CLI `mo --version` calls `getVersionInfo()` directly

Commander's `.version()` accepts a string. Call `getVersionInfo().versionString` at module load time and pass it to `.version()`. Since CLI process exits after printing, the single sync git call is acceptable.

### D5: `mo server status` fetches version from `/api/health`

The `serverStatus()` function currently does a local PID check without calling the server API. To show the server's version (not the CLI's), it should call `GET /api/health` which now includes version fields. This works because `mo server status` already imports `http` and makes requests.

**Implementation:** After confirming server is running via `checkServerHealth()`, make a `GET /api/health` request to get version info and display it. If health check fails, skip version line.

### D6: WebUI reads version from `/api/health`

`GeneralSettingsSection.tsx` already uses react-query hooks. Add a `fetch('/api/health')` call with a simple `useEffect`/`useState` (or a dedicated query hook) to display version + hash. Using `/api/health` rather than `/api/status` because it's lightweight and doesn't require project context.

## Risks / Trade-offs

- [Git not available in npm-installed scenarios] → `gitHash` returns `null`, `versionString` falls back to plain version number. Acceptable degradation.
- [`execSync` blocks event loop briefly] → Called once at startup and cached. Cost is ~20ms for git subprocess. Negligible.
- [`mo server status` now makes HTTP call for version] → Already makes `checkServerHealth()` HTTP call. Adding version fetch is one more lightweight request. If it fails, version line is silently omitted.

## Migration Plan

No migration needed — all changes are additive. API responses gain two new fields (`version`, `gitHash`) that clients can ignore. Deploy by rebuilding (`npm run build`) and restarting server.

## Open Questions

None.
