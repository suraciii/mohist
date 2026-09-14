/**
 * Codex runtime readiness gate.
 *
 * The gate composes five checks, each of which must pass before the
 * Runner claims Codex work:
 *
 *   1. The Codex CLI is executable and its version is within the
 *      locked range (`>=0.153.0 <0.154.0` initially). The locked
 *      compatibility smoke proves the protocol subset still matches
 *      the supported range before the range widens.
 *   2. The spawned `codex app-server --stdio` child started within
 *      the startup budget.
 *   3. The `initialize` → `initialized` handshake completed and the
 *      server's `codexHome` equals the managed state path.
 *   4. Managed Codex authentication is present in
 *      `<codexHome>/auth.json`.
 *   5. The model catalog loaded successfully and contains at least
 *      one entry.
 *
 * A missing executable, missing authentication, or incompatible
 * protocol makes Codex not ready and stops this Runner from claiming
 * Codex work. Each gap is a structured `CodexDiagnostic` so the
 * Runner registration witness can surface an actionable reason on
 * the registration snapshot. The Runner does not fall back to
 * another Runtime.
 *
 * The gate is intentionally pluggable: the CLI probe, the
 * authentication probe, and the catalog loader are passed in by the
 * caller so unit tests can drive the gate deterministically and
 * production code wires the real implementations.
 */

import type { CodexCatalog, CodexClock, CodexDiagnostic, CodexResult } from './types.js'
import { CODEX_SUPPORTED_VERSION_RANGE } from './types.js'
import { normalizeIncompatibleRuntimeCodex, normalizeUnavailableRuntimeCodex } from './errors.js'

export interface CodexCliProbe {
  /**
   * Resolve the absolute path of the Codex CLI. Returns `null` when
   * the CLI cannot be found in the operating system's search path
   * or when the resolved path is not executable.
   */
  resolveCodexBinary(): Promise<string | null>
  /**
   * Return the resolved CLI version. Returns `null` when the CLI did
   * not produce a parseable version string.
   */
  resolveCodexVersion(binaryPath: string): Promise<string | null>
}

export interface CodexAuthenticationProbe {
  /**
   * Returns `true` when managed Codex authentication is present in
   * the managed `codexHome`. The probe MUST read only the managed
   * path; it MUST NOT read a person's default Codex home.
   */
  hasManagedAuthentication(codexHome: string): Promise<boolean>
}

export interface CodexCatalogLoader {
  /**
   * Drive `model/list` over the locked protocol subset. Returns the
   * first non-empty page or `null` when the catalog could not be
   * loaded.
   */
  loadCatalog(): Promise<CodexCatalog | null>
}

export interface CodexReadinessProbe {
  readonly cli: CodexCliProbe
  readonly authentication: CodexAuthenticationProbe
  readonly catalog: CodexCatalogLoader
}

export interface CodexReadinessOptions {
  readonly managedCodexHome: string
  readonly startupTimeoutMs: number
  readonly probe: CodexReadinessProbe
  readonly clock?: CodexClock
}

export interface CodexReadinessOutcome {
  readonly ready: true
  readonly cliPath: string
  readonly cliVersion: string
  readonly codexHome: string
  readonly catalog: CodexCatalog
}

/**
 * Compose the readiness gate. Returns `CodexResult<CodexReadinessOutcome>`
 * where `ok: true` means every check passed. When any check fails,
 * the result includes an actionable diagnostic.
 *
 * The CLI version check uses the locked compatibility range from
 * `./types.js`; widening the range requires the compatibility smoke
 * proof.
 */
export async function evaluateCodexReadiness(
  options: CodexReadinessOptions,
): Promise<CodexResult<CodexReadinessOutcome>> {
  const diagnostics: CodexDiagnostic[] = []
  const binaryPath = await options.probe.cli.resolveCodexBinary()
  if (!binaryPath) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'cli-not-executable',
      message:
        'Codex CLI was not found on PATH or is not executable; install Codex within the locked range or update ENABLED_AGENT_RUNTIMES',
    }
    diagnostics.push(diagnostic)
    const error = normalizeUnavailableRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const version = await options.probe.cli.resolveCodexVersion(binaryPath)
  if (!version) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'cli-version-unknown',
      message: `Could not determine the Codex CLI version at ${binaryPath}; refusing to claim Codex work`,
    }
    diagnostics.push(diagnostic)
    const error = normalizeUnavailableRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  if (!isCodexVersionSupported(version, CODEX_SUPPORTED_VERSION_RANGE.min, CODEX_SUPPORTED_VERSION_RANGE.max)) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'cli-version-incompatible',
      message: `Codex CLI version ${version} is outside the supported range ${CODEX_SUPPORTED_VERSION_RANGE.min} (exclusive of ${CODEX_SUPPORTED_VERSION_RANGE.max})`,
    }
    diagnostics.push(diagnostic)
    const error = normalizeIncompatibleRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const authenticated = await options.probe.authentication.hasManagedAuthentication(options.managedCodexHome)
  if (!authenticated) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'managed-auth-missing',
      message: `No managed Codex authentication found at ${options.managedCodexHome}/auth.json; authenticate the operator before claiming Codex work`,
    }
    diagnostics.push(diagnostic)
    const error = normalizeUnavailableRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  const catalog = await options.probe.catalog.loadCatalog()
  if (!catalog || catalog.models.length === 0) {
    const diagnostic: CodexDiagnostic = {
      severity: 'error',
      code: 'catalog-empty',
      message: 'Codex model catalog loaded empty or failed to load; refusing to claim Codex work',
    }
    diagnostics.push(diagnostic)
    const error = normalizeUnavailableRuntimeCodex(diagnostics)
    return { ok: false, error, diagnostics: error.diagnostics }
  }
  return {
    ok: true,
    value: {
      ready: true,
      cliPath: binaryPath,
      cliVersion: version,
      codexHome: options.managedCodexHome,
      catalog,
    },
    diagnostics,
  }
}

/**
 * Inclusive lower bound, exclusive upper bound on the semantic
 * version. Returns `false` when the candidate cannot be parsed.
 *
 * The Codex CLI ships CalVer-like triples (e.g. `0.153.0`); the
 * comparator treats them as numeric semantic versions.
 */
export function isCodexVersionSupported(candidate: string, minInclusive: string, maxExclusive: string): boolean {
  const parsed = parseCodexSemver(candidate)
  if (!parsed) return false
  const min = parseCodexSemver(minInclusive)
  const max = parseCodexSemver(maxExclusive)
  if (!min || !max) return false
  return compareCodexSemver(parsed, min) >= 0 && compareCodexSemver(parsed, max) < 0
}

interface CodexSemver {
  readonly parts: readonly number[]
}

function parseCodexSemver(value: string): CodexSemver | null {
  if (typeof value !== 'string') return null
  const trimmed = value.trim().replace(/^v/i, '')
  if (trimmed.length === 0) return null
  // Pre-release and build metadata are ignored — the CLI ships
  // numeric CalVer-style versions and the comparator works on the
  // numeric prefix only. Anything beyond the numeric tokens is
  // accepted as long as the prefix parses.
  const numericPrefix = trimmed.split(/[-+]/)[0] ?? ''
  const tokens = numericPrefix.split('.')
  const parts: number[] = []
  for (const token of tokens) {
    if (token.length === 0) return null
    const parsed = Number.parseInt(token, 10)
    if (!Number.isFinite(parsed) || parsed < 0) return null
    if (String(parsed) !== token) return null
    parts.push(parsed)
  }
  if (parts.length === 0) return null
  return { parts }
}

function compareCodexSemver(left: CodexSemver, right: CodexSemver): number {
  const length = Math.max(left.parts.length, right.parts.length)
  for (let index = 0; index < length; index += 1) {
    const a = left.parts[index] ?? 0
    const b = right.parts[index] ?? 0
    if (a !== b) return a < b ? -1 : 1
  }
  return 0
}
