import { describe, expect, it } from 'vitest'
import { processAction, scriptAction, scriptFailureMessage } from './built-in-core.js'
import { withTestRunnerResources } from '../../tests/support/test-resources.js'
import { makeHost } from '../../tests/support/action-host-test.js'

describe('core/script timeout enforcement', () => {
  it('PassesTheConfiguredPositiveFiniteTimeoutToRunCommand', async () => {
    let captured: unknown
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: 'echo ok', timeout: 600_000 }, makeHost())

        expect(result).toMatchObject({ output: { exitCode: 0 } })
        expect(captured).toEqual({ timeoutMs: 600_000 })
      },
      {
        commandRunner: {
          run: async (_command, _args, _cwd, _signal, _env, options) => {
            captured = options
            return { exitCode: 0, stdout: '', stderr: '' }
          },
        },
      },
    )
  })

  it('MapsAnOverBudgetCommandToTheTimeoutErrorCodeRetainingLaneDiagnostics', async () => {
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: 'set -e\nnpm ci', timeout: 5_000 }, makeHost())

        expect(result.error?.code).toBe('timeout')
        expect(result.exitCode).toBe(143)
        // The failure message preserves the command outcome and the captured
        // stdout/stderr diagnostic tails the Server lane projection reads.
        const message = result.error?.message ?? ''
        expect(message).toContain('exit code 143')
        expect(message).toContain('set -e')
        expect(message).toContain('stdout:\npartial-output')
        expect(message).toContain('Command timed out after 5s')
      },
      {
        commandRunner: {
          run: async () => ({
            exitCode: 143,
            stdout: 'partial-output',
            stderr: 'Command timed out after 5s\n',
            status: 'timeout' as const,
            timeoutMs: 5_000,
          }),
        },
      },
    )
  })

  it('MapsATimeoutWithZeroExitCodeToTimeoutInsteadOfSuccess', async () => {
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: 'trap \"exit 0\" TERM', timeout: 5_000 }, makeHost())

        expect(result.error?.code).toBe('timeout')
        expect(result.exitCode).toBe(0)
        expect(result.output).toBeUndefined()
      },
      {
        commandRunner: {
          run: async () => ({
            exitCode: 0,
            stdout: '',
            stderr: 'Command timed out after 5s\n',
            status: 'timeout' as const,
            timeoutMs: 5_000,
          }),
        },
      },
    )
  })

  it('KeepsOrdinaryScriptFailuresAsScriptFailedWithStrictDiagnostics', async () => {
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: 'npm ci', timeout: 600_000 }, makeHost())

        expect(result.error?.code).toBe('script-failed')
        expect(result.exitCode).toBe(7)
        const message = result.error?.message ?? ''
        expect(message).toContain('exit code 7')
        expect(message).toContain('stderr:\nboom')
        expect(message).not.toContain('Command timed out')
      },
      {
        commandRunner: {
          run: async () => ({ exitCode: 7, stdout: '', stderr: 'boom' }),
        },
      },
    )
  })
})

describe('core/script failure diagnostics', () => {
  it('does not inject a hidden resource profile into command execution', async () => {
    let captured: unknown
    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: 'echo ok' }, makeHost())

        expect(result).toMatchObject({ output: { exitCode: 0 } })
        expect(captured).toEqual({ timeoutMs: undefined })
      },
      {
        commandRunner: {
          run: async (_command, _args, _cwd, _signal, _env, options) => {
            captured = options
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
