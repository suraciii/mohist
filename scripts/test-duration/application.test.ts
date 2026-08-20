import assert from 'node:assert/strict'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'

import { main, parseArgs, type ApplicationRuntime } from './application.js'

test('test:app parses one application or help', () => {
  assert.deepEqual(parseArgs(['server']), { application: 'server', help: false })
  assert.deepEqual(parseArgs(['--help']), { application: undefined, help: true })
  assert.throws(() => parseArgs(['server', 'web']), /exactly one application/)
  assert.throws(() => parseArgs(['--track', 'server-unit']), /unknown test:app argument/)
})

test('test:app help derives the application list without building', async () => {
  const reports: string[] = []
  const runtime = {
    now: () => 1000,
    pid: () => 7,
    createArtifactRoot: () => {
      throw new Error('help must not create an artifact root')
    },
    writeFile: () => {
      throw new Error('help must not write evidence')
    },
    runPhase: async () => {
      throw new Error('help must not build')
    },
    runGuard: async () => {
      throw new Error('help must not run tracks')
    },
    report: (line: string) => reports.push(line),
  } satisfies ApplicationRuntime

  assert.equal(await main(['--help'], runtime), 0)
  assert.match(reports[0] ?? '', /server \(/)
  assert.match(reports[0] ?? '', /runner \(/)
})

test('test:app builds once and hands the same run root to the application guard', async () => {
  const artifactRoot = mkdtempSync(join(tmpdir(), 'mohist-test-app-'))
  const writes = new Map<string, string>()
  const phases: string[] = []
  let guardArgs: readonly string[] | undefined
  const runtime = {
    now: () => 1000,
    pid: () => 7,
    createArtifactRoot: () => artifactRoot,
    writeFile: (path: string, content: string) => writes.set(path, content),
    runPhase: async (name: string) => {
      phases.push(name)
      return { exitCode: 0, timedOut: false, cleanupComplete: true }
    },
    runGuard: async (argv: readonly string[]) => {
      guardArgs = argv
      return 0
    },
    report: () => {},
  } satisfies ApplicationRuntime

  assert.equal(await main(['web'], runtime), 0)
  assert.deepEqual(phases, ['build-1'])
  assert.deepEqual(guardArgs, [
    '--application',
    'web',
    '--run-root',
    artifactRoot,
    '--require-build-stamp',
    '--require-enforced',
    '--suite-deadline-at-ms',
    '301000',
  ])
  assert.match(writes.get(join(artifactRoot, 'build-stamp.json')) ?? '', /"runId": "1000-7"/)
})

test('test:app rejects an unknown application before creating diagnostics', async () => {
  let created = false
  const runtime = {
    now: () => 1000,
    pid: () => 7,
    createArtifactRoot: () => {
      created = true
      return '/tmp/unused'
    },
    writeFile: () => {},
    runPhase: async () => ({ exitCode: 0, timedOut: false, cleanupComplete: true }),
    runGuard: async () => 0,
    report: () => {},
  } satisfies ApplicationRuntime

  assert.equal(await main(['missing-app'], runtime), 2)
  assert.equal(created, false)
})
