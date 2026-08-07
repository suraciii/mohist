import { mkdirSync, readFileSync, writeFileSync } from "node:fs"

export const RUNNER_CREDENTIAL_FILE = "credential"

export function runnerCredentialPath(runnerRoot: string): string {
  return `${runnerRoot}/${RUNNER_CREDENTIAL_FILE}`
}

/**
 * Loads the runner's machine credential from
 * <c>$RUNNER_ROOT/credential</c>. Returns null when the file does not
 * exist yet (fresh install awaiting registration); a corrupt read
 * propagates — a credential that cannot be read must not be silently
 * replaced by a re-registration.
 */
export function loadRunnerCredential(runnerRoot: string): string | null {
  try {
    const value = readFileSync(runnerCredentialPath(runnerRoot), "utf8").trim()
    return value.length > 0 ? value : null
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return null
    throw error
  }
}

/**
 * Persists the machine credential with owner-only permissions (0600).
 * The credential is the runner's identity; the file is written before
 * the first authenticated request so a crash between registration and
 * first use never loses it.
 */
export function writeRunnerCredential(runnerRoot: string, credential: string): void {
  mkdirSync(runnerRoot, { recursive: true })
  writeFileSync(runnerCredentialPath(runnerRoot), `${credential}\n`, { mode: 0o600 })
}

/**
 * Exchanges the one-time enrollment token (injected by
 * <c>mo install runner</c>) for the runner's machine credential bound to
 * its RunnerId. The full credential value appears in exactly one
 * response; the caller persists it immediately.
 */
export async function registerWithEnrollmentToken(
  serverUrl: string,
  runnerId: string,
  hostname: string,
  enrollmentToken: string,
  signal?: AbortSignal,
): Promise<string> {
  const response = await fetch(`${serverUrl.replace(/\/$/, "")}/api/runners/register`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ token: enrollmentToken, runnerId, hostname }),
    signal,
  })
  if (!response.ok) {
    throw new Error(
      `runner registration with enrollment token failed: ${response.status} ${await response.text()}; re-run 'mo install runner'`,
    )
  }
  const payload = (await response.json()) as { data?: { token?: unknown } }
  const credential = payload.data?.token
  if (typeof credential !== "string" || credential.length === 0) {
    throw new Error("runner registration returned a malformed response")
  }
  return credential
}

export interface RunnerCredentialResolution {
  serverUrl: string
  runnerId: string
  runnerRoot: string
  hostname: string
  enrollmentToken?: string
  signal?: AbortSignal
}

/**
 * Resolves the credential the runner will present on every server call:
 * the persisted one when present, otherwise a fresh registration through
 * the enrollment token. Returns null when neither exists — the runner
 * then runs unauthenticated and the server rejects its requests.
 */
export async function resolveRunnerCredential(
  resolution: RunnerCredentialResolution,
): Promise<string | null> {
  const existing = loadRunnerCredential(resolution.runnerRoot)
  if (existing) return existing
  if (!resolution.enrollmentToken) return null

  const credential = await registerWithEnrollmentToken(
    resolution.serverUrl,
    resolution.runnerId,
    resolution.hostname,
    resolution.enrollmentToken,
    resolution.signal,
  )
  writeRunnerCredential(resolution.runnerRoot, credential)
  return credential
}
