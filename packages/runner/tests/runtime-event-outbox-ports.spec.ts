import { describe, expect, it } from 'vitest'
import { NodeRuntimeEventOutboxFileSystem } from '../src/server/runtime-event-outbox-ports.js'
import { withRunnerResources } from '../src/system/filesystem.js'
import { MemoryFileSystem } from './support/memory-filesystem.js'

type FailurePoint = 'write' | 'rename'

class FailingAtomicFileSystem extends MemoryFileSystem {
  readonly removedPaths: string[] = []

  constructor(
    private readonly failurePoint: FailurePoint,
    private readonly failure: Error,
    private readonly deleteFailure: Error | null = null,
  ) {
    super()
  }

  override async writeText(path: string, content: string, options?: { mode?: number }): Promise<void> {
    await super.writeText(path, content, options)
    if (this.failurePoint === 'write') throw this.failure
  }

  async seedText(path: string, content: string): Promise<void> {
    await super.writeText(path, content)
  }

  override async rename(source: string, destination: string): Promise<void> {
    if (this.failurePoint === 'rename') throw this.failure
    await super.rename(source, destination)
  }

  override async deleteFile(path: string): Promise<void> {
    this.removedPaths.push(path)
    if (this.deleteFailure) throw this.deleteFailure
    await super.deleteFile(path)
  }
}

function errorWithCode(code: string): Error & { code: string } {
  const error = new Error(code) as Error & { code: string }
  error.code = code
  return error
}

async function writeWithFailure(fileSystem: FailingAtomicFileSystem, failure: Error, expectTemporaryRemoved = true) {
  const target = '/runner-state/runtime-events.json'
  await withRunnerResources({ fileSystem }, async () => {
    const adapter = new NodeRuntimeEventOutboxFileSystem()
    await fileSystem.seedText(target, 'old snapshot')

    await expect(adapter.writeAtomicText(target, 'new snapshot')).rejects.toBe(failure)

    expect(fileSystem.removedPaths).toHaveLength(1)
    const temporary = fileSystem.removedPaths[0]!
    expect(temporary.startsWith(`${target}.`)).toBe(true)
    expect(temporary.endsWith('.tmp')).toBe(true)
    expect(fileSystem.exists(temporary)).toBe(!expectTemporaryRemoved)
    await expect(fileSystem.readText(target)).resolves.toBe('old snapshot')
  })
}

describe('NodeRuntimeEventOutboxFileSystem', () => {
  it('removes the temporary file after an ENOSPC write failure', async () => {
    const failure = errorWithCode('ENOSPC')
    const fileSystem = new FailingAtomicFileSystem('write', failure)

    await writeWithFailure(fileSystem, failure)
  })

  it('removes the temporary file after a rename failure', async () => {
    const failure = errorWithCode('EIO')
    const fileSystem = new FailingAtomicFileSystem('rename', failure)

    await writeWithFailure(fileSystem, failure)
  })

  it('preserves the original error when temporary-file cleanup fails', async () => {
    const failure = errorWithCode('ENOSPC')
    const fileSystem = new FailingAtomicFileSystem('write', failure, errorWithCode('EACCES'))

    await writeWithFailure(fileSystem, failure, false)
  })
})
