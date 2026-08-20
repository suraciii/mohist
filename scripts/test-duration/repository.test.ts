import assert from 'node:assert/strict'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { test } from 'node:test'

import { main, parseArgs, type RepositoryArgs } from './repository.js'
import type { RepositoryRuntime } from './repository.js'

test('repository executor accepts only help as an argument', () => {
  assert.deepEqual(parseArgs([]), { help: false } satisfies RepositoryArgs)
  assert.deepEqual(parseArgs(['--help']), { help: true } satisfies RepositoryArgs)
  assert.throws(() => parseArgs(['--track', 'server-arch']), /unknown repository scope argument/)
})

test('repository executor runs the plan checks and writes scope evidence', async () => {
  const artifactRoot = mkdtempSync(join(tmpdir(), 'mohist-repository-scope-'))
  const phases: string[] = []
  const writes = new Map<string, string>()
  const runtime = {
    now: () => 1000,
    pid: () => 7,
    createArtifactRoot: () => artifactRoot,
    writeFile: (path: string, content: string) => writes.set(path, content),
    runPhase: async (name: string) => {
      phases.push(name)
      return { exitCode: 0, timedOut: false, cleanupComplete: true }
    },
    runGuard: async () => {
      throw new Error('current plan has no repository Architecture tracks')
    },
    report: () => {},
  } satisfies RepositoryRuntime

  assert.equal(await main([], runtime), 0)
  assert.deepEqual(phases, ['check-1', 'check-2'])
  assert.match(writes.get(join(artifactRoot, 'summary.json')) ?? '', /"scope": "repository"/)
  assert.match(writes.get(join(artifactRoot, 'repository.json')) ?? '', /docs:check/)
})
