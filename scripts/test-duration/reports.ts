import type { ReportFormat, TestCase, TestOutcome } from './types.js'

function attr(block: string, name: string): string | undefined {
  const m = block.match(new RegExp(`\\b${name}="([^"]*)"`))
  return m ? m[1] : undefined
}

function parseDurationHHMMSS(value: string): number {
  const parts = value.split(':').map(Number)
  const [h, m, s] = parts.length === 3 ? parts : [0, parts[0] ?? 0, parts[1] ?? 0]
  return h * 3_600_000 + m * 60_000 + s * 1000
}

function normalizeTrxOutcome(outcome?: string): TestOutcome {
  switch (outcome) {
    case 'Passed':
      return 'passed'
    case 'Failed':
      return 'failed'
    case 'Error':
      return 'error'
    case 'Skipped':
      return 'skipped'
    case 'NotExecuted':
    case 'NotRun':
      return 'not-run'
    default:
      return 'other'
  }
}

const UNIT_RESULT = /<UnitTestResult\b([^>]*)>/g

export function parseTrx(xml: string): TestCase[] {
  const cases: TestCase[] = []
  for (const match of xml.matchAll(UNIT_RESULT)) {
    const attrs = match[1]
    const testName = attr(attrs, 'testName')
    const duration = attr(attrs, 'duration')
    if (!testName || !duration) continue
    cases.push({
      name: testName,
      durationMs: parseDurationHHMMSS(duration),
      outcome: normalizeTrxOutcome(attr(attrs, 'outcome')),
    })
  }
  return cases
}

interface VitestAssertion {
  fullName?: string
  title?: string
  status?: string
  duration?: number
  ancestorTitles?: string[]
}

interface VitestFile {
  name?: string
  assertionResults?: VitestAssertion[]
}

interface VitestReport {
  testResults?: VitestFile[]
}

function normalizeVitestOutcome(status?: string): TestOutcome {
  switch (status) {
    case 'passed':
      return 'passed'
    case 'failed':
      return 'failed'
    case 'skipped':
    case 'pending':
    case 'todo':
      return 'skipped'
    default:
      return 'other'
  }
}

export function parseVitestJson(json: string): TestCase[] {
  const report = JSON.parse(json) as VitestReport
  const cases: TestCase[] = []
  for (const file of report.testResults ?? []) {
    for (const assertion of file.assertionResults ?? []) {
      const name =
        assertion.fullName ??
        [...(assertion.ancestorTitles ?? []), assertion.title].filter(Boolean).join(' ')
      if (!name) continue
      cases.push({
        name,
        file: file.name,
        durationMs: typeof assertion.duration === 'number' ? assertion.duration : 0,
        outcome: normalizeVitestOutcome(assertion.status),
      })
    }
  }
  return cases
}

export function parseReport(format: ReportFormat, content: string): TestCase[] {
  return format === 'trx' ? parseTrx(content) : parseVitestJson(content)
}
