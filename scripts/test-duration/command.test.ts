import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { test } from 'node:test'

import { suiteDeadlines } from './deadline.js'
import { parseSuiteConfig } from './config.js'
import { parseArgs, runCommand, type CommandRuntime } from './command.js'

function config() {
  return parseSuiteConfig(`{
    "suiteDeadlineMs": 300000,
    "tracks": [
      {"id":"server-unit","kind":"report-only","trackType":"behavior","application":"server","specKind":"Design","level":"L0","resources":[],"report":"reports/server.json","reportFormat":"vitest","deadlineMs":1000,"enforce":true,"rules":[{"id":"unit","percentile":95,"percentileMs":50}]},
      {"id":"server-spec","kind":"report-only","trackType":"behavior","application":"server","specKind":"Product","level":"L1","resources":[],"report":"reports/spec.json","reportFormat":"vitest","deadlineMs":1000,"enforce":true,"rules":[{"id":"spec","percentile":95,"percentileMs":500}]},
      {"id":"repository-arch","kind":"report-only","trackType":"architecture","architectureScope":"repository","specKind":"Design","resources":[],"report":"reports/arch.json","reportFormat":"vitest","deadlineMs":1000,"enforce":true,"rules":[{"id":"arch"}]}
    ],
    "plan": {
      "applications":["server"],
      "repositoryScope":"repository",
      "applicationBuilds":{"server":[{"command":"dotnet","args":["build"]}]},
      "repositoryChecks":[{"command":"npm","args":["run","docs:check"]}],
      "fastChecks":[{"command":"npm","args":["run","typecheck:scripts"]}],
      "resourceLanes":[{"id":"default","resources":[],"capacity":2}]
    }
  }`)
}

test('test command accepts only the plan-backed modes', () => {
  assert.deepEqual(parseArgs(['fast']), { mode: 'fast', help: false })
  assert.deepEqual(parseArgs(['portfolio']), { mode: 'portfolio', help: false })
  assert.deepEqual(parseArgs(['--help']), { mode: undefined, help: true })
  assert.throws(() => parseArgs(['server']), /unknown test command argument/)
  assert.throws(() => parseArgs(['fast', 'portfolio']), /exactly one mode/)
})

test('fast command selects L0 and Architecture tracks, while portfolio selects every track', async () => {
  const root = mkdtempSync(join(tmpdir(), 'mohist-command-'))
  const phases: string[] = []
  const guardCalls: string[][] = []
  const runtime: CommandRuntime = {
    now: () => 1000,
    pid: () => 7,
    sourceRevision: () => 'revision-1',
    createArtifactRoot: () => root,
    writeFile: (path, content) => writeFileSync(path, content),
    runPhase: async (name) => {
      phases.push(name)
      return { exitCode: 0, timedOut: false, cleanupComplete: true }
    },
    runGuard: async (args) => {
      guardCalls.push([...args])
      return 0
    },
    report: () => {},
  }
  const deadlines = suiteDeadlines(1000, 300000, 5000)

  assert.equal(
    await runCommand(config(), 'fast', runtime, root, 1000, deadlines, new AbortController().signal, 'revision-1'),
    0,
  )
  assert.deepEqual(guardCalls[0]?.filter((arg) => arg === '--track').length, 2)
  assert.deepEqual(phases, ['build-server-1', 'check-1'])
  assert.match(readFileSync(join(root, 'command-summary.json'), 'utf8'), /"mode": "fast"/)

  phases.length = 0
  assert.equal(
    await runCommand(config(), 'portfolio', runtime, root, 1000, deadlines, new AbortController().signal, 'revision-1'),
    0,
  )
  assert.deepEqual(guardCalls[1]?.filter((arg) => arg === '--track').length, 3)
  assert.deepEqual(phases, ['build-server-1'])
})
