import { PiRuntime, type PiRuntimeDeps } from "./runtime.js"
import { currentRunnerResources } from "../../system/filesystem.js"

export type PiRuntimeFactory = (deps: PiRuntimeDeps) => PiRuntime

const defaultRuntimeFactory: PiRuntimeFactory = (deps) => new PiRuntime(deps)

export function getPiRuntimeFactory(): PiRuntimeFactory {
  return currentRunnerResources()?.piRuntimeFactory ?? defaultRuntimeFactory
}
