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

  it('KeepsTheTemporaryScriptOutsideTheWorkspaceWhileExecutingFromTheWorkspace', async () => {
    const workDir = '/tmp/test-workdir'
    let captured: { args: string[]; cwd: string } | undefined

    await withTestRunnerResources(
      async () => {
        const result = await scriptAction({ run: 'git status --porcelain' }, makeHost({ workDir }))

        expect(result).toMatchObject({ output: { exitCode: 0 } })
        expect(captured?.cwd).toBe(workDir)
        expect(captured?.args).toHaveLength(1)
        expect(captured?.args[0]).not.toMatch(new RegExp(`^${workDir}/`))
        expect(captured?.args[0]).toContain('mohist-script-')
      },
      {
        commandRunner: {
          run: async (_command, args, cwd) => {
            captured = { args: [...args], cwd }
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

describe('core/script result stream truncation', () => {
  async function runWithStreams(stdout: string, stderr: string) {
    const result = await withTestRunnerResources(() => scriptAction({ run: 'emit output' }, makeHost()), {
      commandRunner: { run: async () => ({ exitCode: 0, stdout, stderr }) },
    })
    expect(result.error).toBeUndefined()
    expect(result.exitCode).toBe(0)
    expect(result.output).not.toBeNull()
    return result.output!
  }

  it.each([0, 19_999, 20_000])('KeepsAStdoutStreamOf%sUnitsUnchangedAndFlagsItNotTruncated', async (length) => {
    const stdout = 'o'.repeat(length)

    const output = await runWithStreams(stdout, '')

    expect(output.stdout).toBe(stdout)
    expect(output.stdoutTruncated).toBe(false)
    expect(output.stderrTruncated).toBe(false)
  })

  it.each([0, 19_999, 20_000])('KeepsAStderrStreamOf%sUnitsUnchangedAndFlagsItNotTruncated', async (length) => {
    const stderr = 'e'.repeat(length)

    const output = await runWithStreams('', stderr)

    expect(output.stderr).toBe(stderr)
    expect(output.stderrTruncated).toBe(false)
    expect(output.stdoutTruncated).toBe(false)
  })

  it('KeepsTheFirst20000UnitsOfA20001UnitStdoutStreamAndFlagsOnlyStdoutTruncated', async () => {
    const stdout = 'o'.repeat(20_001)

    const output = await runWithStreams(stdout, '')

    expect(output.stdout).toBe(stdout.slice(0, 20_000))
    expect(output.stdout).toBe('o'.repeat(20_000))
    expect(output.stdoutTruncated).toBe(true)
    expect(output.stderrTruncated).toBe(false)
  })

  it('KeepsTheFirst20000UnitsOfA20001UnitStderrStreamAndFlagsOnlyStderrTruncated', async () => {
    const stderr = 'e'.repeat(20_001)

    const output = await runWithStreams('', stderr)

    expect(output.stderr).toBe(stderr.slice(0, 20_000))
    expect(output.stderr).toBe('e'.repeat(20_000))
    expect(output.stderrTruncated).toBe(true)
    expect(output.stdoutTruncated).toBe(false)
  })

  it('FlagsBothStreamsIndependentlyWhenBothExceedTheBoundary', async () => {
    const stdout = 'o'.repeat(25_000)
    const stderr = 'e'.repeat(20_001)

    const output = await runWithStreams(stdout, stderr)

    expect(output.stdout).toBe(stdout.slice(0, 20_000))
    expect(output.stderr).toBe(stderr.slice(0, 20_000))
    expect(output.stdoutTruncated).toBe(true)
    expect(output.stderrTruncated).toBe(true)
  })

  it('SlicesByJavascriptStringUnitsWithoutNormalizingUnicodeBoundaries', async () => {
    const stdout = '🙂'.repeat(10_001)

    const output = await runWithStreams(stdout, '')

    expect(output.stdout).toBe(stdout.slice(0, 20_000))
    expect(output.stdoutTruncated).toBe(true)
  })

  it('DoesNotTrimOrAnnotateStreamContentBelowTheBoundary', async () => {
    const stdout = '  \n padded output \n  '

    const output = await runWithStreams(stdout, '\twarning\n')

    expect(output.stdout).toBe(stdout)
    expect(output.stderr).toBe('\twarning\n')
    expect(output.stdoutTruncated).toBe(false)
    expect(output.stderrTruncated).toBe(false)
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
      'failed Mohist.Server.Tests.SubmitAsync\nTimed out waiting for: status == Running',
      'npm warn deprecated node-domexception@1.0.0',
    )

    expect(message).toBe(
      'Script failed with exit code 1: set -e\nstdout:\nfailed Mohist.Server.Tests.SubmitAsync\nTimed out waiting for: status == Running\nstderr:\nnpm warn deprecated node-domexception@1.0.0',
    )
  })

  it('keeps the final failure output within the stream limit', () => {
    const message = scriptFailureMessage('dotnet test', 1, `${'x'.repeat(10_100)}final failure`, '')

    expect(message).toContain('stdout:\n[truncated]\n')
    expect(message).toContain('final failure')
  })
})
