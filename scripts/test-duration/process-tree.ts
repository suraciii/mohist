import { spawn } from 'node:child_process'

import { nativeTimeSource } from './time.js'

export interface TimeoutHandle {
  readonly promise: Promise<void>
  readonly cancel: () => void
}

export interface TimerOps {
  readonly now: () => number
  readonly createTimeout: (delayMs: number) => TimeoutHandle
}

export interface TreeChild {
  readonly pid: number
  readonly done: Promise<unknown>
}

export interface TaskkillOperation {
  readonly done: Promise<{ readonly exitCode: number | null }>
  readonly cancel: () => void
}

export interface ProcessTreeOps extends TimerOps {
  readonly platform: NodeJS.Platform
  readonly signalProcessGroup: (pid: number, signal: NodeJS.Signals) => void
  readonly isProcessGroupAlive: (pid: number) => boolean
  readonly startTaskkill: (pid: number) => TaskkillOperation
}

function createNativeTimeout(delayMs: number): TimeoutHandle {
  let timer: ReturnType<typeof setTimeout>
  const promise = new Promise<void>((resolvePromise) => {
    timer = setTimeout(resolvePromise, delayMs)
  })
  return { promise, cancel: () => clearTimeout(timer) }
}

function startNativeTaskkill(pid: number): TaskkillOperation {
  const child = spawn('taskkill', ['/pid', String(pid), '/T', '/F'], {
    stdio: 'ignore',
    windowsHide: true,
  })
  const done = new Promise<{ readonly exitCode: number | null }>((resolvePromise) => {
    let settled = false
    const settle = (exitCode: number | null) => {
      if (settled) return
      settled = true
      resolvePromise({ exitCode })
    }
    child.once('error', () => settle(null))
    child.once('close', (exitCode) => settle(exitCode))
  })
  return {
    done,
    cancel: () => {
      try {
        child.kill('SIGKILL')
      } catch {
        // taskkill has already exited.
      }
    },
  }
}

export const nativeProcessTreeOps: ProcessTreeOps = {
  platform: process.platform,
  now: nativeTimeSource.now,
  createTimeout: createNativeTimeout,
  signalProcessGroup: (pid, signal) => {
    try {
      process.kill(-pid, signal)
    } catch {
      // The group has already exited or is no longer signalable.
    }
  },
  isProcessGroupAlive: (pid) => {
    try {
      process.kill(-pid, 0)
      return true
    } catch (error) {
      // EPERM still means that the group exists; only ESRCH proves it is gone.
      return (error as NodeJS.ErrnoException).code !== 'ESRCH'
    }
  },
  startTaskkill: startNativeTaskkill,
}

async function settlesBefore(deadlineAt: number, operation: Promise<unknown>, ops: TimerOps): Promise<boolean> {
  const remainingMs = deadlineAt - ops.now()
  if (remainingMs <= 0) return false
  const timeout = ops.createTimeout(remainingMs)
  try {
    const settled = await Promise.race([
      operation.then(
        () => true,
        () => true,
      ),
      timeout.promise.then(() => false),
    ])
    // A completion that wins the race after the absolute cutoff is not a
    // terminal state within the canonical wall.
    return settled && ops.now() < deadlineAt
  } finally {
    timeout.cancel()
  }
}

async function waitsUntil(deadlineAt: number, ops: TimerOps): Promise<void> {
  const remainingMs = deadlineAt - ops.now()
  if (remainingMs <= 0) return
  const timeout = ops.createTimeout(remainingMs)
  try {
    await timeout.promise
  } finally {
    timeout.cancel()
  }
}

/**
 * Terminate a lane's complete process tree without allowing cancellation to
 * outlive the shared canonical deadline. Windows uses taskkill /T because a
 * detached Unix process group has no equivalent there.
 */
export async function terminateProcessTree(
  child: TreeChild,
  hardDeadlineAt: number,
  graceMs: number,
  ops: ProcessTreeOps = nativeProcessTreeOps,
): Promise<boolean> {
  if (child.pid <= 1) return settlesBefore(hardDeadlineAt, child.done, ops)

  if (ops.platform === 'win32') {
    const taskkill = ops.startTaskkill(child.pid)
    let taskkillExitCode: number | null | undefined
    const taskkillFinished = await settlesBefore(
      hardDeadlineAt,
      taskkill.done.then((result) => {
        taskkillExitCode = result.exitCode
      }),
      ops,
    )
    if (!taskkillFinished) {
      taskkill.cancel()
      return false
    }
    if (taskkillExitCode !== 0) return false
    return settlesBefore(hardDeadlineAt, child.done, ops)
  }

  ops.signalProcessGroup(child.pid, 'SIGTERM')
  const termDeadlineAt = Math.min(hardDeadlineAt, ops.now() + graceMs)
  if ((await settlesBefore(termDeadlineAt, child.done, ops)) && !ops.isProcessGroupAlive(child.pid)) {
    return true
  }
  if (ops.now() < termDeadlineAt) {
    await waitsUntil(termDeadlineAt, ops)
  }
  if (ops.now() >= hardDeadlineAt) return false

  if (!ops.isProcessGroupAlive(child.pid)) {
    return settlesBefore(hardDeadlineAt, child.done, ops)
  }

  ops.signalProcessGroup(child.pid, 'SIGKILL')
  return (await settlesBefore(hardDeadlineAt, child.done, ops)) && !ops.isProcessGroupAlive(child.pid)
}
