import type { RuntimeRecoveryBinding } from './recovery-receipt.js'

/**
 * The poll envelope identifies a work item, while the runtime binding is
 * established later by the AgentSession coordinator. This registry keeps the
 * physical turn address available to the bounded shutdown path without
 * making the runtime adapters depend on RunnerHost.
 */
export class RuntimeTurnRegistry {
  private readonly bindings = new Map<string, RuntimeRecoveryBinding>()

  register(key: string, binding: RuntimeRecoveryBinding): void {
    this.bindings.set(key, { ...binding })
  }

  update(key: string, patch: Partial<RuntimeRecoveryBinding>): void {
    const current = this.bindings.get(key)
    if (!current) return
    this.bindings.set(key, { ...current, ...patch })
  }

  get(key: string): RuntimeRecoveryBinding | null {
    const binding = this.bindings.get(key)
    return binding ? { ...binding } : null
  }

  remove(key: string): void {
    this.bindings.delete(key)
  }
}
