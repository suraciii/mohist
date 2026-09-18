import { createHash } from 'node:crypto'

export const RUNNER_ENVIRONMENT_ALLOWLIST = [
  'PATH',
  'DOTNET_ROOT',
  'DOTNET_ROOT_X64',
  'GOROOT',
  'GOPATH',
  'JAVA_HOME',
  'ANDROID_HOME',
  'ANDROID_SDK_ROOT',
  'M2_HOME',
  'GRADLE_HOME',
  'NVM_BIN',
  'NVM_PATH',
] as const

export interface RunnerEnvironmentObservation {
  readonly version: string | null
  readonly loadedAt: string
}

/**
 * Derives the version of the environment already loaded by this process.
 * This never starts a shell and never serializes the values themselves.
 */
export function observeRunnerEnvironment(
  environment: Readonly<Record<string, string | undefined>>,
  loadedAt: string,
  platform = process.platform,
): RunnerEnvironmentObservation {
  if (platform !== 'linux') return { version: null, loadedAt }

  const values = new Map<string, string>()
  for (const name of RUNNER_ENVIRONMENT_ALLOWLIST) {
    const value = environment[name]
    if (!value) continue

    if (name === 'PATH') {
      const filtered = filterRunnerPath(value, environment.TMPDIR)
      if (filtered) values.set(name, filtered)
      continue
    }

    if (!isSafeAbsolutePath(value)) return { version: null, loadedAt }
    values.set(name, normalizePath(value))
  }

  if (values.size === 0) return { version: null, loadedAt }

  const canonical = [...values.keys()]
    .sort()
    .map((name) => `${name}=${values.get(name)!}\n`)
    .join('')
  return {
    version: createHash('sha256').update(canonical).digest('hex'),
    loadedAt,
  }
}

function filterRunnerPath(value: string, tmpDir: string | undefined): string {
  const seen = new Set<string>()
  const entries: string[] = []
  for (const raw of value.split(':')) {
    if (!raw || !isSafeAbsolutePath(raw)) continue
    const entry = normalizePath(raw)
    if (isTemporaryPath(entry, tmpDir) || seen.has(entry)) continue
    seen.add(entry)
    entries.push(entry)
  }
  return entries.join(':')
}

function isSafeAbsolutePath(value: string): boolean {
  return !/[\u0000\r\n]/u.test(value) && value.startsWith('/')
}

function normalizePath(value: string): string {
  const parts = value.split('/')
  const normalized: string[] = []
  for (const part of parts) {
    if (!part || part === '.') continue
    if (part === '..') {
      if (normalized.length > 0) normalized.pop()
      continue
    }
    normalized.push(part)
  }
  return `/${normalized.join('/')}`
}

function isTemporaryPath(value: string, tmpDir: string | undefined): boolean {
  if (value.startsWith('/tmp/') || value.startsWith('/var/tmp/') || value.startsWith('/run/user/')) return true
  if (!tmpDir || !isSafeAbsolutePath(tmpDir)) return false
  const normalizedTmpDir = normalizePath(tmpDir).replace(/\/$/u, '')
  return value.startsWith(`${normalizedTmpDir}/`)
}
