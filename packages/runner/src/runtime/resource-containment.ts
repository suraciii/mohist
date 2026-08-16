import type { WorkResourceLimits } from '../core/types.js'
import type { CommandResourceLimits } from '../system/process.js'

export const DEFAULT_WORK_MEMORY_MB = 1024
export const DEFAULT_WORK_WALL_CLOCK_MS = 60 * 60 * 1000
export const DEFAULT_WORK_WATCHDOG_INTERVAL_MS = 250
export const DEFAULT_WORK_TURN_BUDGET_MS = 60 * 60 * 1000

/**
 * Full verification runs the server test host and its child processes, so it
 * needs a larger explicit command budget than the conservative default used
 * for ordinary work. The bound remains finite and is applied only by
 * `core/script` when the workflow opts into this profile.
 */
export const FULL_VERIFY_RESOURCE_PROFILE = 'full-verify'
// Server/.NET verification needs more virtual address space than the default
// per-work bound. Keep this finite; the Runner service's aggregate cgroup
// limit remains the outer protection when several works are active.
export const FULL_VERIFY_MEMORY_MB = 16 * 1024

export interface ResolvedWorkResourceLimits {
  readonly memoryMb: number | null
  readonly wallClockMs: number | null
  readonly watchdogIntervalMs: number
  readonly turnBudgetMs: number | null
}

/**
 * Resolve deployment configuration once at the runner boundary. `null`
 * explicitly disables a bound; an omitted field keeps the conservative
 * runner default. This keeps action and runtime enforcement on one policy.
 */
export function normalizeWorkResourceLimits(value?: WorkResourceLimits | null): ResolvedWorkResourceLimits {
  return {
    memoryMb: positiveOrDefault(value?.memoryMb, DEFAULT_WORK_MEMORY_MB),
    wallClockMs: positiveOrDefault(value?.wallClockMs, DEFAULT_WORK_WALL_CLOCK_MS),
    watchdogIntervalMs:
      positiveOrDefault(value?.watchdogIntervalMs, DEFAULT_WORK_WATCHDOG_INTERVAL_MS) ??
      DEFAULT_WORK_WATCHDOG_INTERVAL_MS,
    turnBudgetMs: positiveOrDefault(value?.turnBudgetMs, DEFAULT_WORK_TURN_BUDGET_MS),
  }
}

function positiveOrDefault(value: number | null | undefined, fallback: number): number | null {
  if (value === null) return null
  return value !== undefined && Number.isFinite(value) && value > 0 ? Math.max(1, Math.floor(value)) : fallback
}

export function minPositive(...values: readonly (number | null | undefined)[]): number | undefined {
  const finite = values.filter(
    (value): value is number => value !== null && value !== undefined && Number.isFinite(value) && value > 0,
  )
  return finite.length > 0 ? Math.min(...finite) : undefined
}

export type ActionResourceProfileResolution =
  | { readonly ok: true; readonly resourceLimits?: CommandResourceLimits }
  | { readonly ok: false; readonly message: string }

export function resolveActionResourceProfile(
  profile: string | undefined,
  base: CommandResourceLimits | undefined,
): ActionResourceProfileResolution {
  if (profile === undefined) return { ok: true, resourceLimits: base }
  if (profile !== FULL_VERIFY_RESOURCE_PROFILE) {
    return {
      ok: false,
      message: `Unsupported resource profile '${profile}'. Supported profiles: ${FULL_VERIFY_RESOURCE_PROFILE}`,
    }
  }

  return {
    ok: true,
    resourceLimits: { ...base, memoryMb: FULL_VERIFY_MEMORY_MB },
  }
}
