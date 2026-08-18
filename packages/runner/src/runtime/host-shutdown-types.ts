import type { ShutdownInFlightEntry } from './host-update-shutdown.js'

/**
 * Closure surface exposed to the execution helpers so the worker pool can
 * stop in-flight work through a fully-bound handoff without depending on
 * private host fields. The concrete implementation lives in
 * {@link createHostShutdown}.
 */
export interface RunnerHostShutdown {
  shutdownInFlight(): Promise<void>
  persistInterrupted(entry: ShutdownInFlightEntry, operationId: string, deliveryBudgetMs?: number): Promise<boolean>
}
