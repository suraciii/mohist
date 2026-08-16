import { describe, expect, it } from 'vitest'
import { processAction, scriptAction, scriptFailureMessage } from './built-in-core.js'
import { setPrlimitAvailabilityForTests, type CommandResourceLimits } from '../system/process.js'
import { FakeProcessSpawner } from '../../tests/support/fake-process.js'
import { withTestRunnerResources } from '../../tests/support/test-resources.js'
import { makeHost } from '../../tests/support/action-host-test.js'

describe('core/resource containment', () => {
  it('projects a contained process into the definite resource-containment action error', async () => {
    const spawner = new FakeProcessSpawner()
    setPrlimitAvailabilityForTests(true)
    try {
      await withTestRunnerResources(
        async () => {
          const resultPromise = processAction(
            { command: 'node', args: [] },
            makeHost({
              resourceLimits: { memoryMb: 8, wallClockMs: null },
            }),
          )
          const child = spawner.children[0]!
          child.emit('exit', 137, 'SIGKILL')
          child.close(137)
          await expect(resultPromise).resolves.toMatchObject({ error: { code: 'resource-containment' } })
        },
        { processSpawner: spawner.spawn, processKiller: () => true },
      )
    } finally {
      setPrlimitAvailabilityForTests(undefined)
    }
  })
})

describe('core/script failure diagnostics', () => {
  it('applies the full-verify profile to the command resource limits', async () => {
    let captured: CommandResourceLimits | undefined
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction(
          { run: 'echo ok', resourceProfile: 'full-verify' },
          makeHost({ resourceLimits: { memoryMb: 1024, wallClockMs: 60_000, watchdogIntervalMs: 250 } }),
        )

        expect(result).toMatchObject({ output: { exitCode: 0 } })
        expect(captured).toEqual({ memoryMb: 4096, wallClockMs: 60_000, watchdogIntervalMs: 250 })
      },
      {
        commandRunner: {
          run: async (_command, _args, _cwd, _signal, _env, options) => {
            captured = (options as { resourceLimits?: CommandResourceLimits } | undefined)?.resourceLimits
            return { exitCode: 0, stdout: '', stderr: '' }
          },
        },
      },
    )
  })

  it('includes stdout failures when stderr only contains a warning', () => {
    const message = scriptFailureMessage(
      'set -e\nnpm ci\ndotnet test',
      1,
      'failed Mohist.Server.SpecTests.SubmitAsync\nTimed out waiting for: status == Running',
      'npm warn deprecated node-domexception@1.0.0',
    )

    expect(message).toBe(
      'Script failed with exit code 1: set -e\nstdout:\nfailed Mohist.Server.SpecTests.SubmitAsync\nTimed out waiting for: status == Running\nstderr:\nnpm warn deprecated node-domexception@1.0.0',
    )
  })

  it('keeps the final failure output within the stream limit', () => {
    const message = scriptFailureMessage('dotnet test', 1, `${'x'.repeat(10_100)}final failure`, '')

    expect(message).toContain('stdout:\n[truncated]\n')
    expect(message).toContain('final failure')
  })
})
