import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'

import { parseSuiteConfig } from './config.js'
import { suiteDeadlines } from './deadline.js'
import { parseArgs, runVerify, type VerifyRuntime } from './verify.js'

function config() {
  return parseSuiteConfig(`{
    "suiteDeadlineMs": 300000,
    "tracks": [
      {"id":"server-unit","kind":"report-only","trackType":"behavior","application":"server","specKind":"Design","level":"L0","resources":[],"report":"reports/server.json","reportFormat":"vitest","deadlineMs":1000,"enforce":true,"rules":[{"id":"unit","percentile":95,"percentileMs":50}]},
      {"id":"web-unit","kind":"report-only","trackType":"behavior","application":"web","specKind":"Design","level":"L0","resources":[],"report":"reports/web.json","reportFormat":"vitest","deadlineMs":1000,"enforce":true,"rules":[{"id":"unit","percentile":95,"percentileMs":50}]}
    ],
    "plan": {
      "applications":["server","web"],
      "repositoryScope":"repository",
      "applicationBuilds":{"server":[{"command":"dotnet","args":["build"]}],"web":[{"command":"npm","args":["run","build"]}]},
      "repositoryChecks":[{"command":"npm","args":["run","docs:check"]}],
      "resourceLanes":[{"id":"default","resources":[],"capacity":2}]
    }
  }`)
}

test('verify parser permits only an external diagnostics parent', () => {
  assert.deepEqual(parseArgs([]), { artifactParent: undefined, help: false })
  assert.deepEqual(parseArgs(['--help']), { artifactParent: undefined, help: true })
  assert.deepEqual(parseArgs(['--artifact-root=/tmp/verify']), { artifactParent: '/tmp/verify', help: false })
  assert.throws(() => parseArgs(['--application', 'server']), /unknown verify argument/)
})

test('verify runs every declared application and Repository scope under one deadline', async () => {
  const root = mkdtempSync(join(tmpdir(), 'mohist-verify-'))
  const phases: string[] = []
  const events: string[] = []
  const writes = new Map<string, string>()
  const runtime: VerifyRuntime = {
    now: () => 1000,
    pid: () => 7,
    sourceIdentity: () => ({ revision: 'revision-1', changes: '' }),
    createArtifactRoot: () => root,
    writeFile: (path, content) => {
      writes.set(path, content)
      writeFileSync(path, content)
    },
    runPhase: async (name) => {
      phases.push(name)
      events.push(name)
      return { exitCode: 0, timedOut: false, cleanupComplete: true }
    },
    runGuard: async (argv) => {
      events.push(`guard:${argv[argv.indexOf('--application') + 1]}`)
      const scope = argv[argv.indexOf('--application') + 1]
      const runRoot = argv[argv.indexOf('--run-root') + 1]
      const trackId = scope === 'server' ? 'server-unit' : 'web-unit'
      writeFileSync(
        join(runRoot, 'summary.json'),
        JSON.stringify({
          passed: true,
          evaluations: [{ trackId, total: 1, passed: true }],
          runs: [{ trackId, exitCode: 0, reportReady: true, cleanupComplete: true, timedOut: false }],
        }),
      )
      return 0
    },
    report: () => {},
  }

  const startedAt = 1000
  const code = await runVerify(
    config(),
    runtime,
    root,
    startedAt,
    suiteDeadlines(startedAt, 300000, 5000),
    new AbortController().signal,
    'revision-1',
  )

  assert.equal(code, 0)
  assert.deepEqual(phases.sort(), ['build-1', 'build-1', 'check-1'])
  assert.deepEqual(events.slice(0, 2), ['build-1', 'build-1'])
  assert.ok(events.indexOf('check-1') > 1)
  assert.ok(events.some((event) => event === 'guard:server'))
  assert.ok(events.some((event) => event === 'guard:web'))
  assert.match(
    writes.get(join(root, 'summary.json')) ?? readFileSync(join(root, 'summary.json'), 'utf8'),
    /"passed": true/,
  )
})
