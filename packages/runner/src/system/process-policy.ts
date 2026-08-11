import type { ChildProcess } from "node:child_process"
import { currentRunnerResources } from "./filesystem.js"

export interface ExternalProcessPolicy {
  assertAllowed(label: string): void
  register(child: ChildProcess): void
}

const productionPolicy: ExternalProcessPolicy = {
  assertAllowed() {},
  register() {},
}

export function assertExternalProcessAllowed(label: string): void {
  (currentRunnerResources()?.externalProcessPolicy ?? productionPolicy).assertAllowed(label)
}

export function registerExternalProcess(child: ChildProcess): void {
  (currentRunnerResources()?.externalProcessPolicy ?? productionPolicy).register(child)
}
