import type { ServerConnection } from '../server/connection.js'
import type { InFlightEntry } from './host-state.js'
import { ManagerExecutionRegistry } from './manager-execution-registry.js'

export async function observeManagerDeploymentEpoch(
  previous: string | null,
  current: string | null,
  invalidate: () => Promise<void>,
): Promise<string | null> {
  if (!current) return previous
  if (previous && current !== previous) await invalidate()
  return current
}

export async function invalidateManagerExecutions(
  inFlight: Iterable<InFlightEntry>,
  registry: ManagerExecutionRegistry,
): Promise<void> {
  for (const entry of inFlight) {
    if (entry.work.projectId !== '__mohist_slack_manager__') continue
    entry.managerInvalidated = true
    entry.controller.abort(new Error('manager deployment epoch changed'))
  }
  await registry.disposeAll()
}

export async function revokeManagerExecution(
  connection: ServerConnection,
  executionId: string,
  signal: AbortSignal,
): Promise<void> {
  if (executionId) await connection.revokeManagerExecution(executionId, signal)
}
