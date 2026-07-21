import type { JsonObject, JsonValue } from "../core/types.js"
import type {
  ActionCapability,
  ActionManifest,
  ActionCapabilitySet,
} from "./manifest.js"
import type { TaskLogger } from "../runtime/task-log.js"
import type { InferInputShape, ValidatedWith } from "./context.js"
import type { ActionResult } from "../core/types.js"
import type { IssueFields } from "./issue-fields.js"

export const ALL_CAPABILITIES: ReadonlySet<ActionCapability> = new Set([
  "agent-turn",
  "issue-fields",
  "workflow-checkpoint",
  "add-tasks",
  "write-vars",
])

export function hasCapability(manifest: ActionManifest, capability: ActionCapability): boolean {
  return manifest.capabilities !== undefined && manifest.capabilities.includes(capability)
}

export function capabilitySet(manifest: ActionManifest): ActionCapabilitySet {
  return manifest.capabilities ? new Set(manifest.capabilities) : new Set()
}

export interface AgentTurnRequest {
  prompt: string
  session?: string
  options?: { model?: string; variant?: string }
  deadlineMs?: number
}

export interface AgentTurn {
  turn(request: AgentTurnRequest): Promise<ActionResult>
}

export interface IssueFieldsHost {
  fields(): Promise<IssueFields>
}

export interface CheckpointHost {
  token(scope: string): Promise<string>
}

export interface ActionHost {
  workDir: string
  signal: AbortSignal
  log: TaskLogger | null
  exec(command: string, args?: string[]): Promise<{ exitCode: number; stdout: string; stderr: string }>
  agent?: AgentTurn
  issue?: IssueFieldsHost
  checkpoint?: CheckpointHost
}

export type ActionInputs = Record<string, JsonValue>

export type ActionHostFor<M extends ActionManifest> = M extends { capabilities: infer C }
  ? C extends ReadonlyArray<ActionCapability>
    ? Omit<ActionHost, "agent" | "issue" | "checkpoint"> & {
        agent?: C extends readonly (infer T)[] ? T extends "agent-turn" ? "agent-turn" extends C ? AgentTurn : never : never : never
        issue?: C extends readonly (infer T)[] ? T extends "issue-fields" ? "issue-fields" extends C ? IssueFieldsHost : never : never : never
        checkpoint?: C extends readonly (infer T)[] ? T extends "workflow-checkpoint" ? "workflow-checkpoint" extends C ? CheckpointHost : never : never : never
      }
    : ActionHost
  : ActionHost

export interface ActionEffects {
  addTasks?: AddTaskEffect[]
  writeVars?: JsonObject
}

export interface AddTaskEffect {
  id: string
  title: string
  uses?: string | null
  with?: JsonObject | null
  expect?: JsonObject | null
}

export interface NormalizedResult {
  kind: "ok"
  output: JsonObject | null
  effects: ActionEffects
}

export interface NormalizedResultRejected {
  kind: "error" | "malformed"
  message: string
}
