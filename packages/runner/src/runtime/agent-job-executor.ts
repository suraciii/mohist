import { errorMessage } from '../core/errors.js'
import type { JsonObject, DispatchWorkItem, WorkItemResult } from '../core/types.js'
import { isObject } from '../core/json.js'
import { parseModelIdentifier, type OpenCodeRuntime } from './opencode/index.js'
import type { PiRuntime } from './pi/index.js'
import type { RuntimeAccessor } from '../server/command-runtime.js'
import type { ServerConnection } from '../server/connection.js'
import type { BindingRecoveryCoordinator } from './binding-recovery.js'
import type { RuntimeTurnRegistry } from './runtime-turn-registry.js'
import { SkillResolver } from './skill-resolver.js'
import { buildExecutionEnvelope } from './execution-envelope.js'
import { inlineSlackCollaborationSkill, readExecutionSourceContext } from './slack-execution-context.js'
import {
  attachmentManifestEnvelope,
  buildAttachmentContext,
  deliverAcceptedAttachments,
  readAttachmentDescriptors,
  type AttachmentDescriptor,
  type DeliveredAttachment,
} from './attachment-delivery.js'
import {
  type NamedWorkspaceManager,
  type NamedWorkspaceRepository,
  WorkspaceHomeClaimedError,
} from './workspace-entity.js'
import { executeOpenCodeTurn, executePiTurn, failureResult, type AgentJobTurnDeps } from './agent-job-turn.js'
import { runnerLogger } from '../system/logger.js'

const executionSourceLog = runnerLogger.child('execution-source')

export { projectTurnToWorkItemResult } from './agent-job-turn.js'

export type ModelRetryWaiter = (delayMs: number, signal: AbortSignal) => Promise<boolean>

export interface AgentJobExecutorOptions {
  /** Source-less dispatches are accepted only during the bounded rollout window. */
  readonly strictExecutionSourceValidation?: boolean
  readonly modelRetryInitialDelayMs?: number
  readonly modelRetryMaxDelayMs?: number
  readonly waitForModelRetry?: ModelRetryWaiter
}

/**
 * Agent-owned execution entry for `ownerKind === "agent-job"` work.
 *
 * Branches on the owner kind BEFORE Action resolution and drives the
 * selected runtime — `PiRuntime` or `OpenCodeRuntime` — directly. No
 * `mohist/opencode` Action contract or removed Action. The AgentJob
 * payload lives in `work.with` as a flat
 * `{ prompt, instructions?, model?, reasoningEffort?, variant?, runtime }` shape —
 * composed at launch time from the resolved Agent snapshot and
 * stable for the lifetime of the in-flight request. `runtime` is
 * snapshotted onto the AgentJob by the server so the executor
 * never re-reads the Agent definition; an in-flight edit of the
 * Agent's backend cannot change this turn's runtime.
 *
 * The returned `WorkItemResult.output` keeps the AgentJob terminal
 * `{ kind, status, runtimeSessionId, model, variant, text, error,
 *   failureCategory? }` shape so `AgentJobGrain.ReportResultAsync`'s
 * success/failure parsing and `FailureCategoryFrom` keep working
 * unchanged. The terminal output's `kind` labels the runtime that
 * actually executed (D4: a Pi-executed job is not mislabeled as
 * `opencode`).
 *
 * The physical Session binding is reported back through the existing
 * `/api/runner/{runnerId}/agent-sessions/{projectId}/{sessionId}/attach`
 * endpoint (`ServerConnection.attachAgentSession`); no new wire is introduced.
 */
export interface AgentJobRuntimeAccessors {
  readonly openCode: RuntimeAccessor<OpenCodeRuntime>
  readonly pi: RuntimeAccessor<PiRuntime>
}

export class AgentJobExecutor {
  constructor(
    private readonly connection: ServerConnection,
    private readonly runtimes: AgentJobRuntimeAccessors,
    private readonly bindingRecoveryCoordinator: BindingRecoveryCoordinator | null = null,
    private readonly defaultWorkDir: string = process.cwd(),
    private readonly skillResolver: SkillResolver = new SkillResolver(),
    private readonly namedWorkspaceManager: NamedWorkspaceManager | null = null,
    private readonly options: AgentJobExecutorOptions = {},
    private readonly runtimeTurnRegistry: RuntimeTurnRegistry | null = null,
  ) {}

  async execute(work: DispatchWorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    if (work.ownerKind !== 'agent-job') {
      return failureResult(
        'invalid-dispatch',
        `AgentJobExecutor received non-agent-job work (ownerKind=${work.ownerKind ?? 'null'})`,
      )
    }

    const payload = work.with ?? null
    const sourceContext = readExecutionSourceContext(payload, {
      strict: this.options.strictExecutionSourceValidation === true,
    })
    if (sourceContext.kind === 'invalid') return failureResult('invalid-input', sourceContext.message)
    if (sourceContext.kind === 'legacy')
      executionSourceLog.warn('accepted source-less AgentJob dispatch through the bounded legacy path')
    const slackContext = sourceContext.slackExecutionContext
    const prompt = readPrompt(payload)
    const attachmentDescriptors = readAttachmentDescriptors(payload)
    if (!prompt && attachmentDescriptors.length === 0) {
      return failureResult(
        'invalid-input',
        "AgentJob requires 'prompt' or at least one accepted attachment in dispatch with-payload",
      )
    }

    const instructions = readOptionalString(payload, 'instructions')
    const skillNames = readSkillNames(payload)

    const runtimeName = readRuntime(payload)
    const modelInput = readOptionalString(payload, 'model')
    const variant = readOptionalString(payload, 'variant')
    const reasoningEffort = readOptionalString(payload, 'reasoningEffort')
    const model = parseModel(modelInput)
    if (modelInput && model.kind === 'failure') {
      return failureResult('invalid-input', `AgentJob ${model.message}`)
    }

    let workspaceBinding: WorkspaceBindingResolution
    try {
      workspaceBinding = await resolveWorkspaceBinding(work, signal, this.namedWorkspaceManager)
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) {
        return failureResult(
          'workspace-home-claimed',
          'AgentJob yielded: the workspace is materialized on another runner; the job retries against the home runner',
        )
      }
      throw error
    }
    if (workspaceBinding.kind === 'invalid') {
      return failureResult(
        'invalid-input',
        "AgentJob requires 'workspace.name' or 'workspace.path' to be a non-empty string when 'workspace' is provided in dispatch variables",
      )
    }
    if (workspaceBinding.kind === 'materialization-failed') {
      return failureResult(
        'workspace-materialization-failed',
        `AgentJob failed to materialize the named workspace: ${workspaceBinding.message}`,
      )
    }
    const workDir = workspaceBinding.kind === 'default' ? this.defaultWorkDir : workspaceBinding.workDir

    const resolvedSkills = await this.skillResolver.resolve(skillNames, workDir)
    if (!resolvedSkills.ok) return failureResult(resolvedSkills.code, resolvedSkills.message)
    const skills = slackContext
      ? [...resolvedSkills.skills, inlineSlackCollaborationSkill(slackContext)]
      : resolvedSkills.skills

    let attachmentDelivery: readonly DeliveredAttachment[]
    try {
      const delivery = await this.resolveAttachments(work, workDir, attachmentDescriptors, signal)
      attachmentDelivery = delivery.attachments
    } catch (error) {
      return failureResult(
        'attachment-delivery-failed',
        `AgentJob failed to resolve attachments: ${errorMessage(error)}`,
      )
    }
    const composed = attachmentManifestEnvelope(
      buildExecutionEnvelope(
        prompt ?? '',
        instructions,
        skills,
        slackContext,
        work.agentSessionStartup,
        workspaceBinding.kind === 'named' ? buildWorkspaceAnchor(workspaceBinding.workDir) : null,
      ),
      attachmentDelivery,
    )

    let binding: BindingResolution
    try {
      binding = await resolveBinding(work, this.connection, signal)
    } catch (error) {
      return failureResult(
        'session-binding-failed',
        `AgentJob failed to resolve the AgentSession binding: ${errorMessage(error)}`,
      )
    }

    if (runtimeName === 'pi') {
      return executePiTurn(
        this.turnDeps(),
        work,
        signal,
        payload,
        composed,
        model,
        modelInput,
        variant,
        reasoningEffort,
        workDir,
        binding,
        skills,
      )
    }
    return executeOpenCodeTurn(
      this.turnDeps(),
      work,
      signal,
      payload,
      composed,
      model,
      modelInput,
      variant,
      reasoningEffort,
      workDir,
      binding,
      skills,
      attachmentDelivery,
    )
  }

  private turnDeps(): AgentJobTurnDeps {
    return {
      connection: this.connection,
      runtimes: this.runtimes,
      bindingRecoveryCoordinator: this.bindingRecoveryCoordinator,
      options: this.options,
      runtimeTurnRegistry: this.runtimeTurnRegistry,
    }
  }

  private async resolveAttachments(
    work: DispatchWorkItem,
    workDir: string,
    descriptors: readonly AttachmentDescriptor[],
    signal: AbortSignal,
  ) {
    if (descriptors.length === 0 || !work.projectId || !work.agentSessionId || !work.initialInputId) {
      return deliverAcceptedAttachments(buildAttachmentContext(this.connection, work, workDir, signal), descriptors)
    }
    return deliverAcceptedAttachments(
      {
        projectId: work.projectId,
        agentSessionId: work.agentSessionId,
        inputId: work.initialInputId,
        workDir,
        connection: this.connection,
        signal,
      },
      descriptors,
    )
  }
}

export type BindingResolution = {
  agentSessionId: string | null
  runnerId: string
  runtime: string | null
  runtimeSessionId: string | null
}

async function resolveBinding(
  work: DispatchWorkItem,
  connection: ServerConnection,
  signal: AbortSignal,
): Promise<BindingResolution> {
  const agentSessionId = work.agentSessionId ?? null
  if (!agentSessionId || !work.projectId) {
    return { agentSessionId: null, runnerId: connection.runnerId, runtime: null, runtimeSessionId: null }
  }
  const opened = await connection.getAgentSession(work.projectId, agentSessionId, signal)
  return {
    agentSessionId,
    runnerId: connection.runnerId,
    runtime: opened?.runtime ?? null,
    runtimeSessionId: opened?.runtimeSessionId ?? null,
  }
}

type WorkspaceBindingResolution =
  | { kind: 'default' }
  | { kind: 'invalid' }
  | { kind: 'path'; workDir: string }
  | { kind: 'named'; workDir: string; projectId: string; workspaceName: string }
  | { kind: 'materialization-failed'; message: string }

// Resolve the execution working directory from the dispatch's
// `variables.workspace`:
//   - `name` (Workspace entity binding): materialize the named
//     workspace's persistent directory and report the home to the
//     server (first writer wins — a claimed home fails the dispatch
//     so the job retries against the home runner);
//   - `path` (legacy free-path binding, routed/workflow dimension):
//     use the path verbatim;
//   - absent: the runner's default working directory.
async function resolveWorkspaceBinding(
  work: DispatchWorkItem,
  signal: AbortSignal,
  namedWorkspaceManager: NamedWorkspaceManager | null,
): Promise<WorkspaceBindingResolution> {
  const ws = work.variables?.['workspace']
  if (ws === undefined) return { kind: 'default' }
  if (!isObject(ws)) return { kind: 'invalid' }

  const name = ws['name']
  if (typeof name === 'string' && name.trim().length > 0) {
    if (!namedWorkspaceManager) return { kind: 'invalid' }
    try {
      const materialized = await namedWorkspaceManager.materialize(
        work.projectId ?? '',
        name,
        readWorkspaceRepositories(ws),
        signal,
      )
      return {
        kind: 'named',
        workDir: materialized.path,
        projectId: work.projectId ?? '',
        workspaceName: name,
      }
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) throw error
      return { kind: 'materialization-failed', message: error instanceof Error ? error.message : String(error) }
    }
  }

  const path = ws['path']
  return typeof path === 'string' && path.trim().length > 0 ? { kind: 'path', workDir: path } : { kind: 'invalid' }
}

// The prompt anchor injected when the execution is bound to a named
// workspace: the working directory is the workspace (all workspace
// files live there, $HOME is off-limits), checkouts belong under
// `repos/`, and work products belong at the workspace root. The layout
// convention is prompt, not platform schema.
function buildWorkspaceAnchor(workDir: string): string {
  return `Working directory: ${workDir}. All workspace files live here — do not search $HOME. Repository checkouts belong under repos/ in this directory; plans, research, and other work products belong at the workspace root.`
}

function readWorkspaceRepositories(ws: Record<string, unknown>): readonly NamedWorkspaceRepository[] {
  const value = ws['repositories']
  if (!Array.isArray(value)) return []
  const repositories: NamedWorkspaceRepository[] = []
  for (const item of value) {
    if (!isObject(item)) continue
    const name = item['name']
    const gitUrl = item['gitUrl']
    if (typeof name === 'string' && name.length > 0 && typeof gitUrl === 'string' && gitUrl.length > 0) {
      repositories.push({ name, gitUrl })
    }
  }
  return repositories
}

function readPrompt(payload: JsonObject | null): string | null {
  const prompt = payload?.['prompt']
  if (typeof prompt === 'string') {
    const trimmed = prompt.trim()
    return trimmed.length > 0 ? prompt : null
  }
  return null
}

function readOptionalString(payload: JsonObject | null, key: string): string | null {
  const value = payload?.[key]
  if (typeof value !== 'string') return null
  return value.length > 0 ? value : null
}

/**
 * Read the runtime selection from the dispatch `with.runtime`. The
 * server snapshots the resolved runtime onto the AgentJob envelope;
 * absent is treated as `opencode` so legacy / partial-rollout
 * dispatches keep their existing behavior.
 */
function readRuntime(payload: JsonObject | null): 'opencode' | 'pi' {
  const value = payload?.['runtime']
  if (value === 'pi') return 'pi'
  return 'opencode'
}

function composePrompt(prompt: string, instructions: string | null): string {
  if (!instructions) return prompt
  return `${instructions}\n\n${prompt}`
}

export type ParsedModel =
  | { kind: 'ok'; value: { providerID: string; modelID: string } }
  | { kind: 'failure'; message: string }
  | { kind: 'absent' }

function parseModel(input: string | null): ParsedModel {
  if (!input) return { kind: 'absent' }
  const parsed = parseModelIdentifier(input)
  if (parsed.kind === 'failure') return { kind: 'failure', message: parsed.message }
  return { kind: 'ok', value: parsed.value }
}

function readSkillNames(payload: JsonObject | null): readonly string[] {
  const value = payload?.['skills']
  if (value === undefined || value === null) return []
  return Array.isArray(value) ? (value as string[]) : [String(value)]
}
