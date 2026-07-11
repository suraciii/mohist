import type { ChildProcess } from "node:child_process"

export interface ExternalProcessPolicy {
  assertAllowed(label: string): void
  register(child: ChildProcess): void
}

const productionPolicy: ExternalProcessPolicy = {
  assertAllowed() {},
  register() {},
}

let externalProcessPolicy: ExternalProcessPolicy = productionPolicy

export function assertExternalProcessAllowed(label: string): void {
  externalProcessPolicy.assertAllowed(label)
}

export function registerExternalProcess(child: ChildProcess): void {
  externalProcessPolicy.register(child)
}

export function setExternalProcessPolicyForTest(policy: ExternalProcessPolicy | null): void {
  externalProcessPolicy = policy ?? productionPolicy
}
