import { describe, expect, it as vitestIt } from 'vitest'
import { runCommand } from '../src/system/process.js'
import { FakeProcessSpawner } from './support/fake-process.js'
import { withTestRunnerResources } from './support/test-resources.js'

describe('runCommand execution bounds', () => {
  vitestIt('passes the requested command through without hidden resource wrapping', async () => {
    const spawner = new FakeProcessSpawner()
    await withTestRunnerResources(
      async () => {
        const result = runCommand('node', ['burn.js', 'arg'], '/workspace', new AbortController().signal)
        const child = spawner.children[0]!
        expect(spawner.calls[0]).toMatchObject({ command: 'node', args: ['burn.js', 'arg'] })
        child.emit('exit', 137, 'SIGKILL')
        child.close(137)
        await expect(result).resolves.toMatchObject({ exitCode: 137 })
      },
      { processSpawner: spawner.spawn, processKiller: () => true },
    )
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
