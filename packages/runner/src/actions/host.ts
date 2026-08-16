import type { AgentExecutionDefinition, JsonObject, JsonValue } from '../core/types.js'
import type { ActionCapability, ActionManifest, ActionCapabilitySet } from './manifest.js'
import type { TaskLogger } from '../runtime/task-log.js'
import type { ActionResult } from '../core/types.js'
import type { IssueFields } from './issue-fields.js'
import type { PiRuntime } from '../runtime/pi/index.js'
import type { SkillResolver } from '../runtime/skill-resolver.js'
import type { RuntimeTurnRegistry } from '../runtime/runtime-turn-registry.js'

export const ALL_CAPABILITIES: ReadonlySet<ActionCapability> = new Set([
  'agent-turn',
  'issue-fields',
  'workflow-checkpoint',
  'add-tasks',
  'write-vars',
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
  options?: { model?: string; variant?: string; reasoningEffort?: string }
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
  /** Internal bounded worktree-cleanup attempt, never part of action input. */
  cleanupAttempt?: number | null
  piRuntime?: PiRuntime | null
  skillResolver?: SkillResolver
  agentDefinition?: AgentExecutionDefinition | null
  runtimeTurnRegistry?: RuntimeTurnRegistry | null
  runtimeTurnKey?: string | null
  exec(command: string, args?: string[]): Promise<{ exitCode: number; stdout: string; stderr: string }>
  agent?: AgentTurn
  issue?: IssueFieldsHost
  checkpoint?: CheckpointHost
}

export type ActionInputs = Record<string, JsonValue>

type ActionHostBase = Omit<ActionHost, 'agent' | 'issue' | 'checkpoint'>

type ManifestCapability<M extends ActionManifest> = M extends {
  readonly capabilities: infer C extends ReadonlyArray<ActionCapability>
}
  ? C[number]
  : never

type IsBroadActionManifest<M extends ActionManifest> = [ActionManifest] extends [M]
  ? [M] extends [ActionManifest]
    ? true
    : false
  : false

type CapabilityHost<C> = ('agent-turn' extends C ? { agent: AgentTurn } : { agent?: never }) &
  ('issue-fields' extends C ? { issue: IssueFieldsHost } : { issue?: never }) &
  ('workflow-checkpoint' extends C ? { checkpoint: CheckpointHost } : { checkpoint?: never })

export type ActionHostFor<M extends ActionManifest> =
  IsBroadActionManifest<M> extends true ? ActionHost : ActionHostBase & CapabilityHost<ManifestCapability<M>>

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
  kind: 'ok'
  output: JsonObject | null
  effects: ActionEffects
}

export interface NormalizedResultRejected {
  kind: 'error' | 'malformed'
  message: string
}
