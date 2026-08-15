import { createHash } from 'node:crypto'

import type {
  CurrentExecutionIdentity,
  ExecutionLedger,
  ExecutionLedgerCase,
  ExecutionLedgerExpectation,
  ExecutionLedgerProvenance,
  ExecutionLedgerValidation,
  ExecutionManifest,
  ExecutionManifestCase,
  TestCase,
} from './types.js'

export const EXECUTION_TIME_SOURCE = 'xunit.v3.ITestResultMessage.ExecutionTime' as const
export const EXECUTION_TIME_UNIT = 'seconds' as const

// xunit.v3.common.xml and xunit.v3.core.xml define this field as the time
// spent running the test, in seconds; zero is valid when no test code ran and
// cleanup can make the value partial. TRX wall duration is never substituted.

export interface DiscoveryProcess {
  readonly listTests: () => string
}

export interface GateClock {
  readonly now: () => number
}

export interface CurrentExecutionIdentityReader {
  readonly readAssemblySha256: (assemblyPath: string) => string | Promise<string>
  readonly readSourceSha256: (sourceRoots: readonly string[]) => string | Promise<string>
  readonly readDiscovery: () => Promise<string>
}

export async function readCurrentExecutionIdentity(
  input: {
    readonly assemblyPath: string
    readonly sourceRoots: readonly string[]
    readonly parallelism: string
  },
  reader: CurrentExecutionIdentityReader,
): Promise<CurrentExecutionIdentity> {
  const [assemblySha256, sourceSha256, discovery] = await Promise.all([
    reader.readAssemblySha256(input.assemblyPath),
    reader.readSourceSha256(input.sourceRoots),
    reader.readDiscovery(),
  ])
  if (!/^[0-9a-f]{64}$/i.test(assemblySha256))
    throw new Error('current assembly reader returned an invalid SHA-256 digest')
  if (!/^[0-9a-f]{64}$/i.test(sourceSha256)) throw new Error('current source reader returned an invalid SHA-256 digest')
  return {
    manifest: manifestFromDiscovery(discovery),
    assemblyPath: input.assemblyPath,
    assemblySha256,
    sourceSha256,
    parallelism: input.parallelism,
  }
}

export function validateCurrentExecutionIdentity(
  saved: ExecutionLedgerExpectation,
  current: CurrentExecutionIdentity,
): readonly string[] {
  const errors: string[] = []
  if (
    saved.manifest.hash !== current.manifest.hash ||
    saved.manifest.cases.length !== current.manifest.cases.length ||
    saved.manifest.cases.some((item, index) => JSON.stringify(item) !== JSON.stringify(current.manifest.cases[index]))
  ) {
    errors.push('saved execution provenance discovery does not match the current executable')
  }
  if (saved.assemblyPath !== current.assemblyPath) {
    errors.push('saved execution provenance assembly path does not match the current build')
  }
  if (saved.assemblySha256 !== current.assemblySha256) {
    errors.push('saved execution provenance assembly hash does not match the current build')
  }
  if (saved.sourceSha256 !== current.sourceSha256) {
    errors.push('saved execution provenance source hash does not match the current source tree')
  }
  if (saved.parallelism !== current.parallelism) {
    errors.push('saved execution provenance parallelism does not match the current invocation')
  }
  return errors
}

function sha256(value: string): string {
  return createHash('sha256').update(value, 'utf8').digest('hex')
}

function manifestFromCases(cases: readonly ExecutionManifestCase[]): ExecutionManifest {
  if (cases.length === 0) throw new Error('compiled discovery returned no test cases')
  const ordered = [...cases].sort((left, right) => (left.uid < right.uid ? -1 : left.uid > right.uid ? 1 : 0))
  const uids = new Set<string>()
  for (const item of ordered) {
    if (!/^[0-9a-f]{64}$/i.test(item.uid))
      throw new Error(`compiled discovery returned invalid test case UID ${item.uid}`)
    if (uids.has(item.uid)) throw new Error(`compiled discovery returned duplicate test case UID ${item.uid}`)
    if (!item.name || !item.className || !item.methodName)
      throw new Error(`compiled discovery returned incomplete metadata for ${item.uid}`)
    uids.add(item.uid)
  }
  return { cases: ordered, hash: sha256(JSON.stringify(ordered)) }
}

function provenanceFromExpectation(expected: ExecutionLedgerExpectation): ExecutionLedgerProvenance {
  return {
    schemaVersion: 2,
    runId: expected.runId,
    manifestHash: expected.manifest.hash,
    manifestCount: expected.manifest.cases.length,
    manifestCases: expected.manifest.cases,
    assemblyPath: expected.assemblyPath,
    assemblySha256: expected.assemblySha256,
    sourceSha256: expected.sourceSha256,
    parallelism: expected.parallelism,
  }
}

export function serializeExecutionProvenance(expected: ExecutionLedgerExpectation): string {
  return JSON.stringify(provenanceFromExpectation(expected))
}

export function parseExecutionProvenance(json: string): ExecutionLedgerExpectation {
  let raw: unknown
  try {
    raw = JSON.parse(json) as unknown
  } catch (error) {
    throw new Error(`execution provenance is not valid JSON: ${(error as Error).message}`)
  }
  if (!isRecord(raw)) throw new Error('execution provenance root must be an object')
  const errors: string[] = []
  if (raw.schemaVersion !== 2) errors.push('schemaVersion must be 2')
  const runId = requiredString(raw, 'runId', errors)
  const manifestHash = requiredString(raw, 'manifestHash', errors)
  const manifestCount = raw.manifestCount
  if (typeof manifestCount !== 'number' || !Number.isInteger(manifestCount) || manifestCount <= 0) {
    errors.push('manifestCount must be a positive integer')
  }
  const manifestCases: ExecutionManifestCase[] = []
  if (!Array.isArray(raw.manifestCases)) {
    errors.push('manifestCases must be an array')
  } else {
    for (const [index, value] of raw.manifestCases.entries()) {
      if (!isRecord(value)) {
        errors.push(`manifest case ${index} must be an object`)
        continue
      }
      const uid = requiredString(value, 'uid', errors)
      const name = requiredString(value, 'name', errors)
      const className = requiredString(value, 'className', errors)
      const methodName = requiredString(value, 'methodName', errors)
      manifestCases.push({ uid, name, className, methodName })
    }
  }
  const assemblyPath = requiredString(raw, 'assemblyPath', errors)
  const assemblySha256 = requiredString(raw, 'assemblySha256', errors)
  const sourceSha256 = requiredString(raw, 'sourceSha256', errors)
  const parallelism = requiredString(raw, 'parallelism', errors)
  if (manifestHash && !/^[0-9a-f]{64}$/i.test(manifestHash)) errors.push('manifestHash must be a SHA-256 hex digest')
  if (assemblySha256 && !/^[0-9a-f]{64}$/i.test(assemblySha256))
    errors.push('assemblySha256 must be a SHA-256 hex digest')
  if (sourceSha256 && !/^[0-9a-f]{64}$/i.test(sourceSha256)) errors.push('sourceSha256 must be a SHA-256 hex digest')
  let manifest: ExecutionManifest | undefined
  try {
    manifest = manifestFromCases(manifestCases)
  } catch (error) {
    errors.push((error as Error).message)
  }
  if (manifest && manifest.hash !== manifestHash) errors.push('manifestHash does not match manifestCases')
  if (manifest && manifest.cases.length !== manifestCount) errors.push('manifestCount does not match manifestCases')
  if (errors.length > 0 || !manifest) throw new Error(`execution provenance contract failed: ${errors.join('; ')}`)
  return { runId, manifest, assemblyPath, assemblySha256, sourceSha256, parallelism }
}

export function manifestFromDiscovery(output: string): ExecutionManifest {
  let raw: unknown
  try {
    raw = JSON.parse(output) as unknown
  } catch (error) {
    throw new Error(`compiled discovery is not valid JSON: ${(error as Error).message}`)
  }
  if (!Array.isArray(raw)) throw new Error('compiled discovery root must be an array')
  const cases: ExecutionManifestCase[] = raw.map((value, index) => {
    if (!isRecord(value)) throw new Error(`compiled discovery case ${index} must be an object`)
    const uid = value.ID
    const name = value.DisplayName
    const className = value.Class
    const methodName = value.Method
    if (
      typeof uid !== 'string' ||
      typeof name !== 'string' ||
      typeof className !== 'string' ||
      typeof methodName !== 'string'
    ) {
      throw new Error(`compiled discovery case ${index} has unsupported metadata`)
    }
    return { uid, name, className, methodName }
  })
  return manifestFromCases(cases)
}

export function discoverManifest(process: DiscoveryProcess): ExecutionManifest {
  return manifestFromDiscovery(process.listTests())
}

export function createExecutionRunId(clock: GateClock, idFactory: () => string): string {
  const now = clock.now()
  if (!Number.isFinite(now) || now < 0 || now > Number.MAX_SAFE_INTEGER) {
    throw new Error('gate clock returned an invalid timestamp')
  }
  const timestamp = Math.floor(now)
  const id = idFactory()
  if (!id) throw new Error('run id factory returned an empty value')
  return `${timestamp.toString(36)}-${id}`
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function requiredString(record: Record<string, unknown>, key: string, errors: string[]): string {
  const value = record[key]
  if (typeof value !== 'string' || value.trim().length === 0) {
    errors.push(`ledger field "${key}" must be a non-empty string`)
    return ''
  }
  return value
}

function requiredNonNegativeNumber(record: Record<string, unknown>, key: string, errors: string[]): number {
  const value = record[key]
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) {
    errors.push(`ledger field "${key}" must be a finite non-negative number`)
    return 0
  }
  return value
}

function parseCase(value: unknown, index: number, errors: string[]): ExecutionLedgerCase | undefined {
  if (!isRecord(value)) {
    errors.push(`ledger case ${index} must be an object`)
    return undefined
  }
  const uid = requiredString(value, 'uid', errors)
  const testCaseUid = requiredString(value, 'testCaseUid', errors)
  const name = requiredString(value, 'name', errors)
  const className = requiredString(value, 'className', errors)
  const collectionName = requiredString(value, 'collectionName', errors)
  const outcome = value.outcome
  if (outcome !== 'passed' && outcome !== 'failed' && outcome !== 'skipped' && outcome !== 'not-run') {
    errors.push(`ledger case ${index} has unsupported outcome`)
  }
  const executionTimeSeconds = requiredNonNegativeNumber(value, 'executionTimeSeconds', errors)
  const startTime = requiredString(value, 'startTime', errors)
  const finishTime = requiredString(value, 'finishTime', errors)
  const startMs = startTime ? Date.parse(startTime) : Number.NaN
  const finishMs = finishTime ? Date.parse(finishTime) : Number.NaN
  if (startTime && Number.isNaN(startMs)) errors.push(`ledger case ${index} has invalid startTime`)
  if (finishTime && Number.isNaN(finishMs)) errors.push(`ledger case ${index} has invalid finishTime`)
  if (Number.isFinite(startMs) && Number.isFinite(finishMs) && finishMs < startMs) {
    errors.push(`ledger case ${index} finishTime precedes startTime`)
  }
  const executionTimeMs = executionTimeSeconds * 1000
  if (!Number.isSafeInteger(Math.round(executionTimeMs * 1_000_000))) {
    errors.push(`ledger case ${index} execution time is outside safe precision`)
  }
  if (errors.some((error) => error.includes(`ledger case ${index}`))) return undefined
  return {
    uid,
    testCaseUid,
    name,
    outcome: outcome as ExecutionLedgerCase['outcome'],
    executionTimeMs,
    startTime,
    finishTime,
    className,
    collectionName,
  }
}

export function parseExecutionLedger(json: string): ExecutionLedgerValidation & { ledger?: ExecutionLedger } {
  const errors: string[] = []
  let raw: unknown
  try {
    raw = JSON.parse(json) as unknown
  } catch (error) {
    return { cases: [], errors: [`ledger is not valid JSON: ${(error as Error).message}`] }
  }
  if (!isRecord(raw)) return { cases: [], errors: ['ledger root must be an object'] }

  if (raw.schemaVersion !== 2) errors.push('ledger schemaVersion must be 2')
  if (raw.durationSource !== EXECUTION_TIME_SOURCE)
    errors.push(`ledger durationSource must be ${EXECUTION_TIME_SOURCE}`)
  if (raw.durationUnit !== EXECUTION_TIME_UNIT) errors.push(`ledger durationUnit must be ${EXECUTION_TIME_UNIT}`)
  const runId = requiredString(raw, 'runId', errors)
  const manifestHash = requiredString(raw, 'manifestHash', errors)
  const manifestCountValue = raw.manifestCount
  if (typeof manifestCountValue !== 'number' || !Number.isInteger(manifestCountValue) || manifestCountValue <= 0) {
    errors.push('ledger manifestCount must be a positive integer')
  }
  const manifestCount = typeof manifestCountValue === 'number' ? manifestCountValue : 0
  const assemblyPath = requiredString(raw, 'assemblyPath', errors)
  const assemblySha256 = requiredString(raw, 'assemblySha256', errors)
  const sourceSha256 = requiredString(raw, 'sourceSha256', errors)
  const xunitVersion = requiredString(raw, 'xunitVersion', errors)
  const mtpVersion = requiredString(raw, 'mtpVersion', errors)
  const parallelism = requiredString(raw, 'parallelism', errors)
  if (manifestHash && !/^[0-9a-f]{64}$/i.test(manifestHash))
    errors.push('ledger manifestHash must be a SHA-256 hex digest')
  if (assemblySha256 && !/^[0-9a-f]{64}$/i.test(assemblySha256))
    errors.push('ledger assemblySha256 must be a SHA-256 hex digest')
  if (sourceSha256 && !/^[0-9a-f]{64}$/i.test(sourceSha256))
    errors.push('ledger sourceSha256 must be a SHA-256 hex digest')
  if (xunitVersion === 'unknown' || mtpVersion === 'unknown') errors.push('ledger framework versions must be known')
  const rawCases = raw.cases
  if (!Array.isArray(rawCases) || rawCases.length === 0) {
    errors.push('ledger cases must be a non-empty array')
  }
  const cases: ExecutionLedgerCase[] = []
  for (const [index, value] of (Array.isArray(rawCases) ? rawCases : []).entries()) {
    const parsed = parseCase(value, index, errors)
    if (parsed) cases.push(parsed)
  }

  const uids = new Set<string>()
  const names = new Set<string>()
  for (const item of cases) {
    if (uids.has(item.uid)) errors.push(`ledger contains duplicate uid ${item.uid}`)
    if (names.has(item.name)) errors.push(`ledger contains duplicate test name ${item.name}`)
    uids.add(item.uid)
    names.add(item.name)
  }
  if (errors.length > 0) return { cases: [], errors }

  return {
    cases: cases.map((item) => ({
      name: item.name,
      uid: item.uid,
      durationMs: item.executionTimeMs,
      outcome: item.outcome === 'passed' ? 'passed' : item.outcome === 'failed' ? 'failed' : 'skipped',
    })),
    errors,
    ledger: {
      schemaVersion: 2,
      runId,
      manifestHash,
      manifestCount,
      assemblyPath,
      assemblySha256,
      sourceSha256,
      xunitVersion,
      mtpVersion,
      parallelism,
      durationSource: EXECUTION_TIME_SOURCE,
      durationUnit: EXECUTION_TIME_UNIT,
      cases,
    },
  }
}

export function validateExecutionEvidence(
  trxCases: readonly TestCase[],
  parsed: ExecutionLedgerValidation & { ledger?: ExecutionLedger },
  expected: ExecutionLedgerExpectation,
): ExecutionLedgerValidation {
  const errors = [...parsed.errors]
  const ledger = parsed.ledger
  if (!ledger) return { cases: [], errors }

  if (ledger.runId !== expected.runId) errors.push('execution ledger run ID does not match the current run')
  if (ledger.manifestHash !== expected.manifest.hash)
    errors.push('execution ledger manifest hash does not match compiled discovery')
  if (ledger.manifestCount !== expected.manifest.cases.length)
    errors.push('execution ledger manifest count does not match compiled discovery')
  if (ledger.assemblyPath !== expected.assemblyPath)
    errors.push('execution ledger assembly path does not match the current build')
  if (ledger.assemblySha256 !== expected.assemblySha256)
    errors.push('execution ledger assembly hash does not match the current build')
  if (ledger.sourceSha256 !== expected.sourceSha256)
    errors.push('execution ledger source hash does not match the current source tree')
  if (ledger.parallelism !== expected.parallelism)
    errors.push('execution ledger parallelism does not match the current invocation')

  const manifestByUid = new Map(expected.manifest.cases.map((item) => [item.uid, item]))
  const coveredManifestUids = new Set<string>()
  const trxByName = new Map<string, TestCase>()
  for (const item of trxCases) {
    if (trxByName.has(item.name)) errors.push(`TRX contains duplicate test name ${item.name}`)
    trxByName.set(item.name, item)
  }
  const ledgerByName = new Map(ledger.cases.map((item) => [item.name, item]))
  if (trxByName.size !== ledger.cases.length) errors.push('TRX test count does not match execution ledger')
  if (ledgerByName.size !== ledger.cases.length) errors.push('execution ledger test names are not unique')

  for (const ledgerCase of ledger.cases) {
    const manifestCase = manifestByUid.get(ledgerCase.testCaseUid)
    if (!manifestCase) {
      errors.push(`execution ledger contains undiscovered test case UID ${ledgerCase.testCaseUid}`)
      continue
    }
    coveredManifestUids.add(ledgerCase.testCaseUid)
    if (ledgerCase.className !== manifestCase.className) {
      errors.push(`execution ledger class does not match discovery for test case UID ${ledgerCase.testCaseUid}`)
    }
    const trx = trxByName.get(ledgerCase.name)
    if (!trx) errors.push(`TRX is missing executed test ${ledgerCase.name}`)
    const expectedOutcome =
      ledgerCase.outcome === 'passed' ? 'passed' : ledgerCase.outcome === 'failed' ? 'failed' : 'skipped'
    if (trx && trx.outcome !== expectedOutcome)
      errors.push(`TRX and execution ledger outcome mismatch for ${ledgerCase.name}`)
    if (ledgerCase.outcome === 'skipped' || ledgerCase.outcome === 'not-run')
      errors.push(`CLI execution ledger contains non-executed test ${ledgerCase.name}`)
  }
  for (const item of expected.manifest.cases) {
    if (!coveredManifestUids.has(item.uid))
      errors.push(`execution ledger is missing discovered test case UID ${item.uid}`)
  }
  for (const name of trxByName.keys())
    if (!ledgerByName.has(name)) errors.push(`TRX contains test missing from execution ledger ${name}`)

  return errors.length > 0 ? { cases: [], errors } : { cases: parsed.cases, errors: [] }
}

export function buildLedgerEnvironment(input: {
  readonly runId: string
  readonly manifest: ExecutionManifest
  readonly ledgerPath: string
  readonly assemblyPath: string
  readonly assemblySha256: string
  readonly sourceSha256: string
  readonly parallelism: string
}): Readonly<Record<string, string>> {
  return {
    MOHIST_EXECUTION_LEDGER_PATH: input.ledgerPath,
    MOHIST_EXECUTION_LEDGER_RUN_ID: input.runId,
    MOHIST_EXECUTION_LEDGER_MANIFEST_HASH: input.manifest.hash,
    MOHIST_EXECUTION_LEDGER_MANIFEST_COUNT: String(input.manifest.cases.length),
    MOHIST_EXECUTION_LEDGER_ASSEMBLY_PATH: input.assemblyPath,
    MOHIST_EXECUTION_LEDGER_ASSEMBLY_SHA256: input.assemblySha256,
    MOHIST_EXECUTION_LEDGER_SOURCE_SHA256: input.sourceSha256,
    MOHIST_EXECUTION_LEDGER_PARALLELISM: input.parallelism,
  }
}
