export interface LaneSpec {
  readonly id: string
  readonly dependsOn?: readonly string[]
  readonly resources?: readonly string[]
}

export interface RunningLane<T> {
  readonly result: Promise<T>
  readonly cancel: () => void | Promise<void>
}

export type LaneState = 'passed' | 'failed' | 'cancelled'

export interface ScheduledLane<T> {
  readonly lane: LaneSpec
  readonly state: LaneState
  readonly result?: T
}

export interface ScheduleResult<T> {
  readonly lanes: readonly ScheduledLane<T>[]
  readonly failureLaneId?: string
  readonly aborted: boolean
}

export interface ScheduleOptions {
  readonly resourceLimits?: Readonly<Record<string, number>>
  readonly abort?: AbortSignal
}

interface ActiveLane<T> {
  readonly lane: LaneSpec
  readonly running: RunningLane<T>
  cancelRequested: boolean
}

interface Completion<T> {
  readonly id: string
  readonly result: T
}

interface Rejection {
  readonly id: string
}

function assertPlan(lanes: readonly LaneSpec[], resourceLimits: Readonly<Record<string, number>>): void {
  const ids = new Set<string>()
  for (const lane of lanes) {
    if (!lane.id) throw new Error('scheduler lane id must not be empty')
    if (ids.has(lane.id)) throw new Error(`duplicate scheduler lane id: ${lane.id}`)
    ids.add(lane.id)
    const resources = lane.resources ?? []
    if (new Set(resources).size !== resources.length) {
      throw new Error(`scheduler lane ${lane.id} claims a resource more than once`)
    }
  }
  for (const lane of lanes) {
    for (const dependency of lane.dependsOn ?? []) {
      if (!ids.has(dependency)) throw new Error(`scheduler lane ${lane.id} depends on unknown lane ${dependency}`)
      if (dependency === lane.id) throw new Error(`scheduler lane ${lane.id} depends on itself`)
    }
  }
  for (const [resource, limit] of Object.entries(resourceLimits)) {
    if (!Number.isInteger(limit) || limit <= 0) {
      throw new Error(`scheduler resource ${resource} must have a positive integer limit`)
    }
  }
}

function resourcesAvailable(
  lane: LaneSpec,
  used: ReadonlyMap<string, number>,
  limits: Readonly<Record<string, number>>,
): boolean {
  return (lane.resources ?? []).every((resource) => (used.get(resource) ?? 0) < (limits[resource] ?? 1))
}

function claim(lane: LaneSpec, used: Map<string, number>): void {
  for (const resource of lane.resources ?? []) {
    used.set(resource, (used.get(resource) ?? 0) + 1)
  }
}

function release(lane: LaneSpec, used: Map<string, number>): void {
  for (const resource of lane.resources ?? []) {
    const next = (used.get(resource) ?? 1) - 1
    if (next <= 0) used.delete(resource)
    else used.set(resource, next)
  }
}

export async function scheduleLanes<T>(
  lanes: readonly LaneSpec[],
  start: (lane: LaneSpec) => RunningLane<T>,
  isSuccess: (result: T) => boolean,
  options: ScheduleOptions = {},
): Promise<ScheduleResult<T>> {
  const limits = options.resourceLimits ?? {}
  assertPlan(lanes, limits)

  const pending = new Set(lanes.map((lane) => lane.id))
  const active = new Map<string, ActiveLane<T>>()
  const completed = new Map<string, ScheduledLane<T>>()
  const used = new Map<string, number>()
  const cancellationPromises: Promise<void>[] = []
  let stopping = false
  let aborted = false
  let failureLaneId: string | undefined
  let removeAbortListener = () => {}
  const abortEvent = options.abort === undefined
    ? undefined
    : new Promise<{ readonly kind: 'abort' }>((resolveAbort) => {
        const onAbort = () => resolveAbort({ kind: 'abort' })
        if (options.abort!.aborted) onAbort()
        else {
          options.abort!.addEventListener('abort', onAbort, { once: true })
          removeAbortListener = () => options.abort!.removeEventListener('abort', onAbort)
        }
      })

  const stop = (failure?: string, wasAborted = false) => {
    if (stopping) return
    stopping = true
    aborted = wasAborted
    failureLaneId = failure
    for (const id of pending) {
      const lane = lanes.find((candidate) => candidate.id === id)!
      completed.set(id, { lane, state: 'cancelled' })
    }
    pending.clear()
    for (const activeLane of active.values()) {
      activeLane.cancelRequested = true
      cancellationPromises.push(
        Promise.resolve()
          .then(() => activeLane.running.cancel())
          .then(() => undefined, () => undefined),
      )
    }
  }

  try {
    if (options.abort?.aborted) stop(undefined, true)
    while (pending.size > 0 || active.size > 0) {
      if (!stopping && options.abort?.aborted) stop(undefined, true)
      if (!stopping) {
      let admitted = true
      while (admitted) {
        if (options.abort?.aborted) {
          stop(undefined, true)
          break
        }
        admitted = false
        for (const lane of lanes) {
          if (options.abort?.aborted) {
            stop(undefined, true)
            break
          }
          if (!pending.has(lane.id)) continue
          if (!(lane.dependsOn ?? []).every((dependency) => completed.get(dependency)?.state === 'passed')) continue
          if (!resourcesAvailable(lane, used, limits)) continue
          pending.delete(lane.id)
          claim(lane, used)
          try {
            active.set(lane.id, { lane, running: start(lane), cancelRequested: false })
          } catch {
            release(lane, used)
            completed.set(lane.id, { lane, state: 'failed' })
            stop(lane.id)
          }
          admitted = true
          break
        }
      }
      }

      if (active.size === 0) {
        if (pending.size === 0) break
        if (stopping) continue
        const waiting = [...pending].join(', ')
        throw new Error(`scheduler deadlock: no runnable lanes among ${waiting}`)
      }

      const completions = [...active.entries()].map(([id, activeLane]) =>
        activeLane.running.result.then(
          (result) => ({ kind: 'completion' as const, id, result }),
          () => ({ kind: 'rejection' as const, id }),
        ),
      )
      const event = await Promise.race(
        abortEvent && !stopping
          ? [...completions, abortEvent]
          : completions,
      )

      if (event.kind === 'abort') {
        stop(undefined, true)
        continue
      }

      if (event.kind === 'rejection') {
        const rejected = event as Rejection & { readonly kind: 'rejection' }
        const activeLane = active.get(rejected.id)
        if (!activeLane) continue
        active.delete(rejected.id)
        release(activeLane.lane, used)
        const state: LaneState = activeLane.cancelRequested ? 'cancelled' : 'failed'
        completed.set(rejected.id, { lane: activeLane.lane, state })
        if (!activeLane.cancelRequested) stop(rejected.id)
        continue
      }

      const completion = event as Completion<T> & { readonly kind: 'completion' }
      const activeLane = active.get(completion.id)
      if (!activeLane) continue
      active.delete(completion.id)
      release(activeLane.lane, used)
      const passed = isSuccess(completion.result)
      const state: LaneState = activeLane.cancelRequested ? 'cancelled' : passed ? 'passed' : 'failed'
      completed.set(completion.id, { lane: activeLane.lane, state, result: completion.result })
      if (!passed && !activeLane.cancelRequested) stop(completion.id)
    }
  } finally {
    removeAbortListener()
  }

  await Promise.allSettled(cancellationPromises)
  return {
    lanes: lanes.map((lane) => completed.get(lane.id) ?? { lane, state: 'cancelled' }),
    failureLaneId,
    aborted,
  }
}
