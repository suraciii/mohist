import { describe, expect, it as vitestIt, vi } from 'vitest'
import { runCommand, setPrlimitAvailabilityForTests, setProcessTreeRssReaderForTests } from '../src/system/process.js'
import { FakeProcessSpawner } from './support/fake-process.js'
import { withTestRunnerResources } from './support/test-resources.js'

describe('runCommand resource containment', () => {
  vitestIt('wraps a bounded command with prlimit while preserving the process seam', async () => {
    const spawner = new FakeProcessSpawner()
    setPrlimitAvailabilityForTests(true)
    setProcessTreeRssReaderForTests(async () => null)
    try {
      await withTestRunnerResources(
        async () => {
          const result = runCommand('node', ['burn.js', 'arg'], '/workspace', new AbortController().signal, undefined, {
            resourceLimits: { memoryMb: 32, wallClockMs: null },
          })
          const child = spawner.children[0]!
          expect(spawner.calls[0]).toMatchObject({
            command: 'prlimit',
            args: ['--as=33554432', '--data=33554432', '--', 'node', 'burn.js', 'arg'],
          })
          child.emit('exit', 137, 'SIGKILL')
          child.close(137)
          await expect(result).resolves.toMatchObject({ resourceContainment: true })
        },
        { processSpawner: spawner.spawn, processKiller: () => true },
      )
    } finally {
      setPrlimitAvailabilityForTests(undefined)
      setProcessTreeRssReaderForTests(undefined)
    }
  })

  vitestIt('kills an over-bound fallback command from the aggregate RSS watchdog', async () => {
    vi.useFakeTimers()
    const spawner = new FakeProcessSpawner()
    const signals: Array<[number, NodeJS.Signals | number]> = []
    setPrlimitAvailabilityForTests(false)
    setProcessTreeRssReaderForTests(async () => 2 * 1024 * 1024)
    try {
      await withTestRunnerResources(
        async () => {
          const result = runCommand('node', [], '/workspace', new AbortController().signal, undefined, {
            resourceLimits: { memoryMb: 1, wallClockMs: null, watchdogIntervalMs: 10 },
          })
          const child = spawner.children[0]!
          expect(spawner.calls[0]?.command).toBe('node')
          await vi.advanceTimersByTimeAsync(10)
          expect(signals).toEqual([[-4242, 'SIGTERM']])
          child.emit('exit', 143, 'SIGTERM')
          child.close(143)
          await expect(result).resolves.toMatchObject({ resourceContainment: true })
        },
        {
          processSpawner: spawner.spawn,
          processKiller: (pid, signal) => {
            signals.push([pid, signal ?? 'SIGTERM'])
            return true
          },
        },
      )
    } finally {
      setPrlimitAvailabilityForTests(undefined)
      setProcessTreeRssReaderForTests(undefined)
      vi.useRealTimers()
    }
  })
})

describe('runCommand output', () => {
  const it = (name: string, body: (spawner: FakeProcessSpawner) => Promise<void>) =>
    vitestIt(name, () => {
      const spawner = new FakeProcessSpawner()
      return withTestRunnerResources(() => body(spawner), { processSpawner: spawner.spawn })
    })

  it('StreamsLinesAndPreservesAggregateOutput', async (spawner) => {
    const lines: string[] = []
    const result = runCommand('command', ['--flag'], '/workspace', new AbortController().signal, undefined, {
      onLine: (line) => lines.push(line),
    })
    const child = spawner.children[0]!

    child.writeStdout('out-1\nout-2')
    child.writeStderr('err-1\n')
    child.close(0)

    await expect(result).resolves.toEqual({
      exitCode: 0,
      stdout: 'out-1\nout-2',
      stderr: 'err-1\n',
    })
    expect(lines).toEqual(['out-1', 'err-1', 'out-2'])
    expect(spawner.calls).toHaveLength(1)
  })

  it('DecodesSplitUtf8AndFlushesTrailingLinesBeforeClose', async (spawner) => {
    const lines: string[] = []
    const closes: number[] = []
    const result = runCommand('command', [], '/workspace', new AbortController().signal, undefined, {
      onLine: (line) => lines.push(line),
      onClose: (code) => closes.push(code),
    })
    const child = spawner.children[0]!
    const bytes = Buffer.from('file-文件\n')

    child.writeStdout(bytes.subarray(0, 7))
    child.writeStdout(bytes.subarray(7))
    child.writeStderr('tail')
    child.close(7)

    await expect(result).resolves.toMatchObject({ exitCode: 7, stdout: 'file-文件\n', stderr: 'tail' })
    expect(lines).toEqual(['file-文件', 'tail'])
    expect(closes).toEqual([7])
  })
})
