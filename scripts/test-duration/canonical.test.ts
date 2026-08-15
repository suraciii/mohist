import assert from 'node:assert/strict'
import { resolve, sep } from 'node:path'
import { test } from 'node:test'

import { createArtifactRoot, main, parseArgs, type CanonicalGateRuntime, type PhaseResult } from './canonical.js'

interface RuntimeProbe {
  readonly root: string
  readonly artifactParents: readonly (string | undefined)[]
  readonly phases: Array<{
    readonly name: string
    readonly artifactRoot: string
    readonly executionDeadlineAt: number
    readonly hardDeadlineAt: number
  }>
  readonly durationArgs: readonly string[][]
  readonly durationClock: readonly (() => number)[]
  readonly writes: readonly string[]
  readonly reports: readonly string[]
  readonly sourceIdentityCalls: () => number
}

function fakeRuntime(
  phaseResults: readonly PhaseResult[],
  durationResult: number,
  phaseAdvances: readonly number[] = [],
): { runtime: CanonicalGateRuntime; probe: RuntimeProbe } {
  const root = '/system-temp/mohist-canonical-gate-1000-42-fixed'
  const artifactParents: Array<string | undefined> = []
  const phases: Array<{ name: string; artifactRoot: string; executionDeadlineAt: number; hardDeadlineAt: number }> = []
  const durationArgs: string[][] = []
  const durationClock: Array<() => number> = []
  const writes: string[] = []
  const reports: string[] = []
  let sourceIdentityCalls = 0
  let now = 1000
  let phaseIndex = 0
  const probe: RuntimeProbe = {
    root,
    artifactParents,
    phases,
    durationArgs,
    durationClock,
    writes,
    reports,
    sourceIdentityCalls: () => sourceIdentityCalls,
  }
  const runtime: CanonicalGateRuntime = {
    now: () => now,
    pid: () => 42,
    sourceIdentity: () => {
      sourceIdentityCalls += 1
      return { revision: 'candidate-revision', changes: '' }
    },
    createArtifactRoot: (runId, artifactParent) => {
      assert.equal(runId, '1000-42')
      artifactParents.push(artifactParent)
      return root
    },
    writeFile: (path) => {
      writes.push(path)
    },
    runPhase: async (name, _command, _args, artifactRoot, deadlines, phaseNow) => {
      assert.equal(phaseNow(), now)
      phases.push({
        name,
        artifactRoot,
        executionDeadlineAt: deadlines.executionDeadlineAt,
        hardDeadlineAt: deadlines.hardDeadlineAt,
      })
      const result = phaseResults[phaseIndex]
      now += phaseAdvances[phaseIndex] ?? 0
      phaseIndex += 1
      if (!result) throw new Error(`unexpected phase: ${name}`)
      return result
    },
    runDurationGate: async (argv, guardRuntime) => {
      durationArgs.push([...argv])
      durationClock.push(guardRuntime.now)
      return durationResult
    },
    report: (line) => {
      reports.push(line)
    },
  }
  return { runtime, probe }
}

test('createArtifactRoot uses a unique system temporary directory or explicit external parent, never the repository', () => {
  const prefixes: string[] = []
  const ops = {
    tempDirectory: () => '/system-temp',
    makeDirectory: (prefix: string) => {
      prefixes.push(prefix)
      return `${prefix}fixed-${prefixes.length}`
    },
  }

  const defaultRoot = createArtifactRoot('1000-42', ops)
  const explicitRoot = createArtifactRoot('1000-43', '/external-diagnostics', ops)

  assert.equal(defaultRoot, '/system-temp/mohist-canonical-gate-1000-42-fixed-1')
  assert.equal(explicitRoot, '/external-diagnostics/mohist-canonical-gate-1000-43-fixed-2')
  assert.deepEqual(prefixes, [
    '/system-temp/mohist-canonical-gate-1000-42-',
    '/external-diagnostics/mohist-canonical-gate-1000-43-',
  ])
  assert.ok(!defaultRoot.startsWith(`${resolve(process.cwd())}${sep}`))
  assert.ok(!explicitRoot.startsWith(`${resolve(process.cwd())}${sep}`))
})

test('canonical gate retains an external diagnostic root for success, failure, and timeout', async (t) => {
  const cases: ReadonlyArray<{
    readonly name: string
    readonly phases: readonly PhaseResult[]
    readonly durationResult: number
    readonly expectedCode: number
    readonly expectedPhaseCount: number
    readonly durationRuns: boolean
  }> = [
    {
      name: 'success',
      phases: [
        { exitCode: 0, timedOut: false },
        { exitCode: 0, timedOut: false },
        { exitCode: 0, timedOut: false },
      ],
      durationResult: 0,
      expectedCode: 0,
      expectedPhaseCount: 3,
      durationRuns: true,
    },
    {
      name: 'docs failure',
      phases: [{ exitCode: 1, timedOut: false }],
      durationResult: 0,
      expectedCode: 1,
      expectedPhaseCount: 1,
      durationRuns: false,
    },
    {
      name: 'docs timeout',
      phases: [{ exitCode: null, timedOut: true, cleanupComplete: true }],
      durationResult: 0,
      expectedCode: 1,
      expectedPhaseCount: 1,
      durationRuns: false,
    },
  ]

  for (const scenario of cases) {
    await t.test(scenario.name, async () => {
      const { runtime, probe } = fakeRuntime(scenario.phases, scenario.durationResult)
      const code = await main(runtime)

      assert.equal(code, scenario.expectedCode)
      assert.ok(!probe.root.startsWith(`${resolve(process.cwd())}${sep}`))
      assert.deepEqual(probe.reports, [`canonical-gate diagnostics: ${probe.root}`])
      assert.equal(probe.phases.length, scenario.expectedPhaseCount)
      assert.ok(probe.phases.every((phase) => phase.artifactRoot === probe.root))
      assert.ok(probe.writes.every((path) => path.startsWith(`${probe.root}${sep}`)))
      assert.equal(probe.durationArgs.length, scenario.durationRuns ? 1 : 0)
      assert.deepEqual(probe.artifactParents, [undefined])
      if (scenario.durationRuns) {
        assert.deepEqual(probe.durationArgs[0], [
          '--all',
          '--run-root',
          probe.root,
          '--require-build-stamp',
          '--suite-deadline-at-ms',
          '301000',
        ])
        assert.equal(probe.durationClock[0], runtime.now)
      }
    })
  }
})

test('canonical gate passes its fake clock and absolute deadline into the guard boundary', async () => {
  const { runtime, probe } = fakeRuntime(
    [
      { exitCode: 0, timedOut: false },
      { exitCode: 0, timedOut: false },
      { exitCode: 0, timedOut: false },
    ],
    0,
  )

  const code = await main(runtime)

  assert.equal(code, 0)
  assert.equal(probe.durationClock.length, 1)
  assert.equal(probe.durationClock[0], runtime.now)
  assert.equal(probe.durationClock[0](), 1000)
  assert.equal(probe.durationArgs[0].at(-1), '301000')
})

test('canonical gate uses the injected clock to stop before a new phase at the absolute execution cutoff', async () => {
  const { runtime, probe } = fakeRuntime([{ exitCode: 0, timedOut: false }], 0, [289_000])

  const code = await main(runtime)

  assert.equal(code, 1)
  assert.deepEqual(
    probe.phases.map((phase) => phase.name),
    ['docs'],
  )
  assert.equal(probe.phases[0].executionDeadlineAt, 290_000)
  assert.equal(probe.phases[0].hardDeadlineAt, 301_000)
  assert.deepEqual(probe.durationArgs, [])
})

test('canonical propagates one external termination signal through phases and the duration scheduler', async () => {
  const controller = new AbortController()
  let disposed = false
  const { runtime: base } = fakeRuntime(
    [
      { exitCode: 0, timedOut: false },
      { exitCode: 0, timedOut: false },
      { exitCode: 0, timedOut: false },
    ],
    0,
  )
  const runtime: CanonicalGateRuntime = {
    ...base,
    createTerminationSignal: () => ({
      signal: controller.signal,
      dispose: () => {
        disposed = true
      },
    }),
    runPhase: async (name, _command, _args, _artifactRoot, _deadlines, _now, abortSignal) => {
      if (name === 'docs') assert.equal(abortSignal, controller.signal)
      else assert.notEqual(abortSignal, controller.signal)
      return { exitCode: 0, timedOut: false }
    },
    runDurationGate: async (_argv, guardRuntime) => {
      assert.equal(guardRuntime.abortSignal, controller.signal)
      controller.abort()
      return 0
    },
  }

  assert.equal(await main(runtime), 1)
  assert.equal(disposed, true)
})

test('canonical cancels the sibling build phase when script boundaries fail', async () => {
  const { runtime: base } = fakeRuntime([], 0)
  let buildCancelled = false
  const runtime: CanonicalGateRuntime = {
    ...base,
    runPhase: async (name, _command, _args, _artifactRoot, _deadlines, _now, abortSignal) => {
      if (name === 'docs') return { exitCode: 0, timedOut: false }
      if (name === 'script-boundaries') return { exitCode: 1, timedOut: false, cleanupComplete: true }
      await new Promise<void>((resolvePromise) =>
        abortSignal.addEventListener('abort', () => resolvePromise(), { once: true }),
      )
      buildCancelled = true
      return { exitCode: null, timedOut: false, cancelled: true, cleanupComplete: true }
    },
  }

  assert.equal(await main(runtime), 1)
  assert.equal(buildCancelled, true)
})

test('canonical starts the read-only boundary phase without waiting for build completion', async () => {
  const { runtime: base } = fakeRuntime([], 0)
  let releaseBuild!: () => void
  const buildGate = new Promise<void>((resolvePromise) => {
    releaseBuild = resolvePromise
  })
  let boundaryStarted = false
  const runtime: CanonicalGateRuntime = {
    ...base,
    runPhase: async (name) => {
      if (name === 'docs') return { exitCode: 0, timedOut: false }
      if (name === 'build') {
        await buildGate
        return { exitCode: 0, timedOut: false }
      }
      boundaryStarted = true
      releaseBuild()
      return { exitCode: 0, timedOut: false }
    },
  }

  assert.equal(await main(runtime), 0)
  assert.equal(boundaryStarted, true)
})

test('canonical fails before phases when build inputs are dirty or untracked', async () => {
  const { runtime: base, probe } = fakeRuntime([], 0)
  const runtime: CanonicalGateRuntime = {
    ...base,
    sourceIdentity: () => ({ revision: 'candidate-revision', changes: '?? packages/new-input.ts' }),
  }

  assert.equal(await main(runtime), 1)
  assert.deepEqual(probe.phases, [])
  assert.deepEqual(probe.durationArgs, [])
  assert.ok(probe.writes.some((path) => path.endsWith('fatal-error.json')))
  assert.match(probe.reports.at(-1) ?? '', /requires a clean index and worktree/)
})

test('canonical revalidates the same clean revision through build, boundaries, and duration', async () => {
  const { runtime, probe } = fakeRuntime(
    [
      { exitCode: 0, timedOut: false },
      { exitCode: 0, timedOut: false },
      { exitCode: 0, timedOut: false },
    ],
    0,
  )

  assert.equal(await main(runtime), 0)
  assert.equal(probe.sourceIdentityCalls(), 4)
})

test('canonical parser keeps explicit artifact roots as external parents', () => {
  assert.deepEqual(parseArgs(['--artifact-root', '/external-diagnostics']), { artifactParent: '/external-diagnostics' })
  assert.throws(() => parseArgs(['--unexpected']), /unknown canonical-gate argument/)
})
