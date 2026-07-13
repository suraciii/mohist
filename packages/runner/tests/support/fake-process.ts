import { EventEmitter } from "node:events"
import type { ProcessSpawner } from "../../src/system/process.js"

type SpawnOptions = Parameters<ProcessSpawner>[2]

export class FakeChildProcess extends EventEmitter {
  readonly stdout = new EventEmitter()
  readonly stderr = new EventEmitter()
  readonly killSignals: Array<NodeJS.Signals | number> = []

  constructor(readonly pid = 4242) {
    super()
  }

  kill(signal: NodeJS.Signals | number = "SIGTERM") {
    this.killSignals.push(signal)
    return true
  }

  writeStdout(value: string | Buffer) {
    this.stdout.emit("data", Buffer.isBuffer(value) ? value : Buffer.from(value))
  }

  writeStderr(value: string | Buffer) {
    this.stderr.emit("data", Buffer.isBuffer(value) ? value : Buffer.from(value))
  }

  close(exitCode: number | null) {
    this.emit("close", exitCode)
  }

  fail(error: Error) {
    this.emit("error", error)
  }
}

export class FakeProcessSpawner {
  readonly calls: Array<{ command: string, args: string[], options: SpawnOptions }> = []
  readonly children: FakeChildProcess[] = []

  readonly spawn: ProcessSpawner = (command, args, options) => {
    const child = new FakeChildProcess()
    this.calls.push({ command, args: [...args], options })
    this.children.push(child)
    return child as unknown as ReturnType<ProcessSpawner>
  }
}
