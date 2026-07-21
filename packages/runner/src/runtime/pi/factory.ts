import { PiRuntime, type PiRuntimeDeps } from "./runtime.js"

export type PiRuntimeFactory = (deps: PiRuntimeDeps) => PiRuntime

let runtimeFactory: PiRuntimeFactory = (deps) => new PiRuntime(deps)

export function getPiRuntimeFactory(): PiRuntimeFactory {
  return runtimeFactory
}

export function setPiRuntimeFactoryForTest(factory: PiRuntimeFactory | null): void {
  runtimeFactory = factory ?? ((deps) => new PiRuntime(deps))
}
