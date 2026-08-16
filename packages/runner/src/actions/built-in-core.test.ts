import { describe, expect, it } from 'vitest'
import { processAction, scriptAction, scriptFailureMessage } from './built-in-core.js'
import { withTestRunnerResources } from '../../tests/support/test-resources.js'
import { makeHost } from '../../tests/support/action-host-test.js'

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
