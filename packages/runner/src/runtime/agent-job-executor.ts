import { errorMessage } from "../core/errors.js"
import type {
  JsonObject,
  DispatchWorkItem,
  WorkItemResult,
} from "../core/types.js"
import { isObject } from "../core/json.js"
import {
  parseModelIdentifier,
  type OpenCodeRuntime,
  type RuntimeResult,
  type RuntimeTurnObserver,
  type RuntimeTurnRequest,
  type RuntimeTurnResult,
  type RuntimeFilePart,
} from "./opencode/index.js"
import type {
  PiRuntime,
  PiRuntimeEvent,
  PiResult,
  PiTurnObserver,
  PiTurnRequest,
  PiTurnResult,
} from "./pi/index.js"
import { resolveAccessor, type RuntimeAccessor } from "../server/command-runtime.js"
import type { ServerConnection } from "../server/connection.js"
import { resolveOrRecoverBinding, type BindingRecoveryCoordinator, type RuntimeBinding } from "./binding-recovery.js"
import { SkillResolver, type ResolvedSkill } from "./skill-resolver.js"
import { runnerLogger } from "../system/logger.js"
import { buildExecutionEnvelope } from "./execution-envelope.js"
import { inlineSlackCollaborationSkill, readSlackExecutionContext } from "./slack-execution-context.js"
import {
  attachmentManifestEnvelope,
  buildAttachmentContext,
  deliverAcceptedAttachments,
  readAttachmentDescriptors,
  type AttachmentDescriptor,
  type DeliveredAttachment,
} from "./attachment-delivery.js"
import {
  type NamedWorkspaceManager,
  type NamedWorkspaceRepository,
  WorkspaceHomeClaimedError,
} from "./workspace-entity.js"

const log = runnerLogger.child("job")

/**
 * Agent-owned execution entry for `ownerKind === "agent-job"` work.
 *
 * Branches on the owner kind BEFORE Action resolution and drives the
 * selected runtime — `PiRuntime` or `OpenCodeRuntime` — directly. No
 * `mohist/opencode` Action contract or removed Action. The AgentJob
 * payload lives in `work.with` as a flat
 * `{ prompt, instructions?, model?, variant?, runtime }` shape —
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
  ) {}

  async execute(work: DispatchWorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    if (work.ownerKind !== "agent-job") {
      return failureResult("invalid-dispatch", `AgentJobExecutor received non-agent-job work (ownerKind=${work.ownerKind ?? "null"})`)
    }

    const payload = work.with ?? null
    const prompt = readPrompt(payload)
    const attachmentDescriptors = readAttachmentDescriptors(payload)
    if (!prompt && attachmentDescriptors.length === 0) {
      return failureResult("invalid-input", "AgentJob requires 'prompt' or at least one accepted attachment in dispatch with-payload")
    }

    const instructions = readOptionalString(payload, "instructions")
    const skillNames = readSkillNames(payload)
    const slackContext = readSlackExecutionContext(payload)
    if (slackContext.kind === "invalid")
      return failureResult("invalid-input", slackContext.message)

    const runtimeName = readRuntime(payload)
    const modelInput = readOptionalString(payload, "model")
    const variant = readOptionalString(payload, "variant")
    const model = parseModel(modelInput)
    if (modelInput && model.kind === "failure") {
      return failureResult("invalid-input", `AgentJob ${model.message}`)
    }

    let workspaceBinding: WorkspaceBindingResolution
    try {
      workspaceBinding = await resolveWorkspaceBinding(work, signal, this.namedWorkspaceManager)
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) {
        return failureResult("workspace-home-claimed", "AgentJob yielded: the workspace is materialized on another runner; the job retries against the home runner")
      }
      throw error
    }
    if (workspaceBinding.kind === "invalid") {
      return failureResult("invalid-input", "AgentJob requires 'workspace.name' or 'workspace.path' to be a non-empty string when 'workspace' is provided in dispatch variables")
    }
    if (workspaceBinding.kind === "materialization-failed") {
      return failureResult("workspace-materialization-failed", `AgentJob failed to materialize the named workspace: ${workspaceBinding.message}`)
    }
    const workDir = workspaceBinding.kind === "default" ? this.defaultWorkDir : workspaceBinding.workDir

    const resolvedSkills = await this.skillResolver.resolve(skillNames, workDir)
    if (!resolvedSkills.ok) return failureResult(resolvedSkills.code, resolvedSkills.message)
    const skills = slackContext.kind === "resolved"
      ? [...resolvedSkills.skills, inlineSlackCollaborationSkill(slackContext.value)]
      : resolvedSkills.skills

    let attachmentDelivery: readonly DeliveredAttachment[]
    try {
      const delivery = await this.resolveAttachments(work, workDir, attachmentDescriptors, signal)
      attachmentDelivery = delivery.attachments
    } catch (error) {
      return failureResult("attachment-delivery-failed", `AgentJob failed to resolve attachments: ${errorMessage(error)}`)
    }
    const composed = attachmentManifestEnvelope(
      buildExecutionEnvelope(
        prompt ?? "",
        instructions,
        skills,
        slackContext.kind === "resolved" ? slackContext.value : null,
        work.agentSessionStartup,
        workspaceBinding.kind === "named" ? buildWorkspaceAnchor(workspaceBinding.workDir) : null,
      ),
      attachmentDelivery)

    let binding: BindingResolution
    try {
      binding = await resolveBinding(work, this.connection, signal)
    } catch (error) {
      return failureResult("session-binding-failed", `AgentJob failed to resolve the AgentSession binding: ${errorMessage(error)}`)
    }

    if (runtimeName === "pi") {
      return this.executePi(work, signal, payload, composed, model, modelInput, variant, workDir, binding, skills)
    }
    return this.executeOpenCode(work, signal, payload, composed, model, modelInput, variant, workDir, binding, skills, attachmentDelivery)
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
    return deliverAcceptedAttachments({
      projectId: work.projectId,
      agentSessionId: work.agentSessionId,
      inputId: work.initialInputId,
      workDir,
      connection: this.connection,
      signal,
    }, descriptors)
  }

  private async executeOpenCode(
    work: DispatchWorkItem,
    signal: AbortSignal,
    payload: JsonObject | null,
    composed: string,
    model: ParsedModel,
    modelInput: string | null,
    variant: string | null,
    workDir: string,
    binding: BindingResolution,
    skills: readonly ResolvedSkill[],
    attachments: readonly DeliveredAttachment[],
  ): Promise<WorkItemResult> {
    const runtime = resolveAccessor(this.runtimes.openCode)
    if (!runtime) {
      return failureResult("runtime-unavailable", "AgentJob requires the OpenCode runtime; the runner has not yet established the runtime or it is rebuilding", "opencode")
    }
    if (!runtime.ready()) {
      const diagnostic = runtime.diagnostic()
      return failureResult("runtime-unavailable", `AgentJob requires the OpenCode runtime to be ready: ${diagnostic?.message ?? "no readiness diagnostic"}`, "opencode")
    }

    let selected = binding.runtimeSessionId
    if (binding.agentSessionId && work.projectId && selected && typeof runtime.resolveSession === "function") {
      const expected: RuntimeBinding = {
        runnerId: binding.runnerId,
        runtime: "opencode",
        runtimeSessionId: selected,
        workDir,
      }
      const recovery = await resolveOrRecoverBinding({
        runnerId: this.connection.runnerId,
        expected,
        runtime: { kind: "opencode", runtime },
        probe: async (candidate) => {
          const result = await runtime.resolveSession({ target: { runtime: "opencode", runtimeSessionId: candidate.runtimeSessionId, workDir: candidate.workDir } })
          return result.ok ? { ok: true, activeTurn: result.value.activeTurn } : { ok: false, kind: result.error.kind, message: result.error.message }
        },
        replace: async (current, replacement) => {
          await this.connection.recoverMissingAgentSession(work.projectId!, binding.agentSessionId!, {
            expectedRunnerId: current.runnerId,
            expectedRuntime: current.runtime,
            expectedRuntimeSessionId: current.runtimeSessionId,
            replacementRuntimeSessionId: replacement.runtimeSessionId,
          }, signal)
        },
        model: model.kind === "ok" ? { providerID: model.value.providerID, modelID: model.value.modelID } : null,
        recoveryKey: expected.runtimeSessionId!,
        coordinator: this.bindingRecoveryCoordinator ?? undefined,
      })
      if (!recovery.ok) return failureResult(recovery.kind, recovery.message, "opencode")
      selected = recovery.binding.runtimeSessionId
    }

    const fileParts = attachments
      .filter((entry): entry is Extract<DeliveredAttachment, { status: "delivered" }> => entry.status === "delivered" && entry.filePart !== null)
      .map((entry) => entry.filePart as RuntimeFilePart)

    const turnRequest: RuntimeTurnRequest = {
      target: {
        runtime: "opencode",
        runtimeSessionId: selected,
        workDir,
      },
      prompt: composed,
      ...(fileParts.length > 0 ? { fileParts } : {}),
      options: {
        model: model.kind === "ok" ? { providerID: model.value.providerID, modelID: model.value.modelID } : null,
        variant: variant ?? null,
        ...(skills.length > 0 ? { skills } : {}),
        unknownKeys: collectUnknownKeys(payload),
      },
    }

    const eventSink = createAgentSessionEventSink(this.connection, work, signal, binding.agentSessionId)
    // Issue-512 T-001: when the coordinator durably recorded the
    // initial input on the AgentSession before dispatch, the runner
    // must NOT re-publish a `session.input` runtime event. The
    // durable input identity is owned by the Session aggregate; the
    // dispatch only carries the correlation ids so the runner knows
    // the initial input is already accepted.
    const skipInitialInput = Boolean(work.initialInputId && work.initialTurnId)
    let attachedRuntimeSessionId: string | null = null
    const observer: RuntimeTurnObserver | undefined = binding.agentSessionId
      ? {
        onSessionReady: async (session) => {
          attachedRuntimeSessionId = session.runtimeSessionId
          await eventSink.attachSession(session.runtimeSessionId, session.workDir, modelInput)
          if (!skipInitialInput) {
            await eventSink.publishSessionInput(composed, session.runtimeSessionId)
          }
        },
        onEvent: (event) => {
          eventSink.observeEvent(event)
        },
      }
      : undefined

    let result: RuntimeResult<RuntimeTurnResult>
    try {
      result = await runtime.runTurn(turnRequest, signal, observer)
    } catch (error) {
      return failureResult("turn-failed", `AgentJob turn threw: ${errorMessage(error)}`)
    }
    await eventSink.drain()
    return projectTurnToWorkItemResult(result, "opencode", modelInput, variant)
  }

  private async executePi(
    work: DispatchWorkItem,
    signal: AbortSignal,
    payload: JsonObject | null,
    composed: string,
    model: ParsedModel,
    modelInput: string | null,
    variant: string | null,
    workDir: string,
    binding: BindingResolution,
    skills: readonly ResolvedSkill[],
  ): Promise<WorkItemResult> {
    const runtime = resolveAccessor(this.runtimes.pi)
    if (!runtime) {
      return failureResult("runtime-unavailable", "AgentJob requires the Pi runtime; the runner has not yet established the runtime or it is rebuilding", "pi")
    }
    if (!runtime.ready()) {
      const diagnostic = runtime.diagnostic()
      return failureResult("runtime-unavailable", `AgentJob requires the Pi runtime to be ready: ${diagnostic?.message ?? "no readiness diagnostic"}`, "pi")
    }

    const eventSink = createAgentSessionEventSink(this.connection, work, signal, binding.agentSessionId)
    let runtimeSessionId = binding.runtimeSessionId
    if (binding.agentSessionId && work.projectId && runtimeSessionId && typeof runtime.resolveSession === "function") {
      const expected: RuntimeBinding = { runnerId: binding.runnerId, runtime: "pi", runtimeSessionId, workDir }
      const recovery = await resolveOrRecoverBinding({
        runnerId: this.connection.runnerId,
        expected,
        runtime: { kind: "pi", runtime },
        probe: async (candidate) => {
          const result = await runtime.resolveSession({ target: { runtime: "pi", runtimeSessionId: candidate.runtimeSessionId, workDir: candidate.workDir } })
          return result.ok ? { ok: true, activeTurn: result.value.activeTurn } : { ok: false, kind: result.error.kind, message: result.error.message }
        },
        replace: async (current, replacement) => {
          await this.connection.recoverMissingAgentSession(work.projectId!, binding.agentSessionId!, {
            expectedRunnerId: current.runnerId,
            expectedRuntime: current.runtime,
            expectedRuntimeSessionId: current.runtimeSessionId,
            replacementRuntimeSessionId: replacement.runtimeSessionId,
          }, signal)
        },
        recoveryKey: expected.runtimeSessionId!,
        coordinator: this.bindingRecoveryCoordinator ?? undefined,
      })
      if (!recovery.ok) return failureResult(recovery.kind, recovery.message, "pi")
      runtimeSessionId = recovery.binding.runtimeSessionId
    }
    if (!runtimeSessionId) {
      const created = await runtime.createSession({ target: { runtime: "pi", runtimeSessionId: null, workDir } })
      if (!created.ok) {
        const code = mapPiErrorKind(created.error.kind)
        return failureResult(code, created.error.message, "pi", created.error.diagnostics)
      }
      runtimeSessionId = created.value.runtimeSessionId
    }
    await eventSink.attachSession(runtimeSessionId, workDir, modelInput)
    if (!work.initialInputId || !work.initialTurnId) {
      await eventSink.publishSessionInput(composed, runtimeSessionId)
    }

    const request: PiTurnRequest = {
      target: { runtime: "pi", runtimeSessionId, workDir },
      prompt: composed,
      options: {
        model: model.kind === "ok" ? `${model.value.providerID}/${model.value.modelID}` : null,
        variant: variant ?? null,
        ...(skills.length > 0 ? { skills } : {}),
        unknownKeys: collectUnknownKeys(payload),
      },
    }
    const observer: PiTurnObserver | undefined = binding.agentSessionId
      ? {
        onEvent: (event) => {
          eventSink.observePiEvent(event)
        },
      }
      : undefined

    let result: PiResult<PiTurnResult>
    try {
      result = await runtime.runTurn(request, signal, observer)
    } catch (error) {
      return failureResult("turn-failed", `AgentJob turn threw: ${errorMessage(error)}`)
    }
    await eventSink.drain()
    return projectPiTurnToWorkItemResult(result, "pi", modelInput, variant)
  }

}

type BindingResolution = { agentSessionId: string | null; runnerId: string; runtime: string | null; runtimeSessionId: string | null }

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
  | { kind: "default" }
  | { kind: "invalid" }
  | { kind: "path"; workDir: string }
  | { kind: "named"; workDir: string; projectId: string; workspaceName: string }
  | { kind: "materialization-failed"; message: string }

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
  const ws = work.variables?.["workspace"]
  if (ws === undefined) return { kind: "default" }
  if (!isObject(ws)) return { kind: "invalid" }

  const name = ws["name"]
  if (typeof name === "string" && name.trim().length > 0) {
    if (!namedWorkspaceManager) return { kind: "invalid" }
    try {
      const materialized = await namedWorkspaceManager.materialize(
        work.projectId ?? "",
        name,
        readWorkspaceRepositories(ws),
        signal,
      )
      return {
        kind: "named",
        workDir: materialized.path,
        projectId: work.projectId ?? "",
        workspaceName: name,
      }
    } catch (error) {
      if (error instanceof WorkspaceHomeClaimedError) throw error
      return { kind: "materialization-failed", message: error instanceof Error ? error.message : String(error) }
    }
  }

  const path = ws["path"]
  return typeof path === "string" && path.trim().length > 0
    ? { kind: "path", workDir: path }
    : { kind: "invalid" }
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
  const value = ws["repositories"]
  if (!Array.isArray(value)) return []
  const repositories: NamedWorkspaceRepository[] = []
  for (const item of value) {
    if (!isObject(item)) continue
    const name = item["name"]
    const gitUrl = item["gitUrl"]
    if (typeof name === "string" && name.length > 0 && typeof gitUrl === "string" && gitUrl.length > 0) {
      repositories.push({ name, gitUrl })
    }
  }
  return repositories
}

function readPrompt(payload: JsonObject | null): string | null {
  const prompt = payload?.["prompt"]
  if (typeof prompt === "string") {
    const trimmed = prompt.trim()
    return trimmed.length > 0 ? prompt : null
  }
  return null
}

function readOptionalString(payload: JsonObject | null, key: string): string | null {
  const value = payload?.[key]
  if (typeof value !== "string") return null
  return value.length > 0 ? value : null
}

/**
 * Read the runtime selection from the dispatch `with.runtime`. The
 * server snapshots the resolved runtime onto the AgentJob envelope;
 * absent is treated as `opencode` so legacy / partial-rollout
 * dispatches keep their existing behavior.
 */
function readRuntime(payload: JsonObject | null): "opencode" | "pi" {
  const value = payload?.["runtime"]
  if (value === "pi") return "pi"
  return "opencode"
}

function composePrompt(prompt: string, instructions: string | null): string {
  if (!instructions) return prompt
  return `${instructions}\n\n${prompt}`
}

type ParsedModel =
  | { kind: "ok"; value: { providerID: string; modelID: string } }
  | { kind: "failure"; message: string }
  | { kind: "absent" }

function parseModel(input: string | null): ParsedModel {
  if (!input) return { kind: "absent" }
  const parsed = parseModelIdentifier(input)
  if (parsed.kind === "failure") return { kind: "failure", message: parsed.message }
  return { kind: "ok", value: parsed.value }
}

function collectUnknownKeys(payload: JsonObject | null): readonly string[] | undefined {
  if (!payload || typeof payload !== "object") return undefined
  const known = new Set(["prompt", "instructions", "model", "variant", "runtime", "skills", "attachments", "slackExecutionContext"])
  const unknown: string[] = []
  for (const key of Object.keys(payload)) {
    if (!known.has(key)) unknown.push(key)
  }
  return unknown.length > 0 ? unknown : undefined
}

function readSkillNames(payload: JsonObject | null): readonly string[] {
  const value = payload?.["skills"]
  if (value === undefined || value === null) return []
  return Array.isArray(value) ? value as string[] : [String(value)]
}

interface AgentSessionEventSink {
  attachSession(runtimeSessionId: string, workDir: string, model: string | null): Promise<void>
  publishSessionInput(text: string, runtimeSessionId: string): Promise<void>
  observeEvent(event: { readonly type: string; readonly runtimeSessionId: string; readonly payload: Record<string, unknown> }): void
  observePiEvent(event: PiRuntimeEvent): void
  drain(): Promise<void>
}

function createAgentSessionEventSink(
  connection: ServerConnection,
  work: DispatchWorkItem,
  signal: AbortSignal,
  agentSessionId: string | null,
): AgentSessionEventSink {
  let pending: Promise<void> = Promise.resolve()
  const projectId = work.projectId
  if (!agentSessionId || !projectId) {
    const noop = async () => undefined
    return {
      attachSession: noop,
      publishSessionInput: noop,
      observeEvent: () => undefined,
      observePiEvent: () => undefined,
      drain: noop,
    }
  }
  return {
    async attachSession(runtimeSessionId, workDir, model) {
      try {
        await connection.openAgentSession(
          projectId!,
          agentSessionId,
          { workDir },
          signal,
        )
        await connection.attachAgentSession(
          projectId!,
          agentSessionId,
          {
            runtimeSessionId,
            workDir,
            processPid: null,
            model,
            workId: work.workId,
            agentJobId: work.agentJobId ?? null,
          },
          signal,
        )
      } catch (error) {
        log.error("agent-session open/attach failed", { job: work.agentJobId, session: agentSessionId, exception: error })
        throw error
      }
    },
    async publishSessionInput(text, runtimeSessionId) {
      try {
        await connection.agentSessionRuntimeEvents(
          projectId!,
          agentSessionId,
          {
            workId: work.workId,
            workType: work.workType,
            stage: work.stage,
            runtimeSessionId,
            runtimeEvents: [{
              type: "session.input",
              payload: {
                text,
                kind: "task",
                source: "agent-job",
                role: "user",
                runtimeSessionId,
              },
            }],
          },
          signal,
        )
      } catch (error) {
        log.error("agent-session input publish failed", { job: work.agentJobId, session: agentSessionId, exception: error })
        throw error
      }
    },
    observeEvent(event) {
      pending = pending
        .then(() => connection.agentSessionRuntimeEvents(
          projectId!,
          agentSessionId,
          {
            workId: work.workId,
            workType: work.workType,
            stage: work.stage,
            runtimeSessionId: event.runtimeSessionId,
            runtimeEvents: [{ type: event.type, payload: event.payload }],
          },
          signal,
        ).then(() => undefined))
        .catch((error) => {
          log.error("agent-session runtime event failed", { job: work.agentJobId, session: agentSessionId, exception: error })
        })
    },
    observePiEvent(event) {
      pending = pending
        .then(() => connection.agentSessionRuntimeEvents(
          projectId!,
          agentSessionId,
          {
            workId: work.workId,
            workType: work.workType,
            stage: work.stage,
            runtimeSessionId: event.runtimeSessionId,
            runtimeEvents: [{ type: event.type, payload: event.payload }],
          },
          signal,
        ).then(() => undefined))
        .catch((error) => {
          log.error("agent-session runtime event failed", { job: work.agentJobId, session: agentSessionId, exception: error })
        })
    },
    async drain() {
      await pending
    },
  }
}

function failureResult(
  code: string,
  message: string,
  runtime: "opencode" | "pi" = "opencode",
  diagnostics?: readonly { code: string; message: string }[],
): WorkItemResult {
  return {
    status: "failed",
    message,
    error: { code, message },
    output: diagnostics
      ? buildAgentJobOutput(false, null, runtime, null, null, null, message, diagnostics)
      : undefined,
    exitCode: 1,
  }
}

function buildAgentJobOutput(
  ok: boolean,
  runtimeSessionId: string | null,
  runtime: "opencode" | "pi",
  model: string | null,
  variant: string | null,
  text: string | null,
  error: string | null,
  diagnostics: readonly { code: string; message: string }[],
  hint?: "reset",
): JsonObject {
  return {
    kind: runtime,
    status: ok ? "success" : "failure",
    runtimeSessionId,
    model,
    variant,
    text,
    error,
    diagnostics: diagnostics.map((d) => ({ code: d.code, message: d.message })),
    ...(hint ? { hint } : {}),
  }
}

/**
 * Convert the runtime result directly into the AgentJob-owned work result.
 * This path deliberately does not cross the Workflow Action boundary.
 */
export function projectTurnToWorkItemResult(
  result: RuntimeResult<RuntimeTurnResult>,
  runtime: "opencode" | "pi",
  model: string | null,
  variant: string | null,
): WorkItemResult {
  if (!result.ok) {
    const error = result.error
    const output = buildAgentJobOutput(
      false,
      null,
      runtime,
      model,
      variant,
      null,
      error.message,
      result.diagnostics,
      error.kind === "missing-session" ? "reset" : undefined,
    )
    return {
      status: "failed",
      message: error.message,
      error: { code: error.kind, message: error.message },
      output,
      exitCode: 1,
    }
  }
  const facts = result.value.facts
  const output = buildAgentJobOutput(
    true,
    facts.runtimeSessionId,
    runtime,
    model,
    variant,
    facts.finalAssistantText,
    null,
    result.value.diagnostics,
  )
  return {
    status: "completed",
    message: "AgentJob completed",
    output,
    exitCode: 0,
  }
}

export function projectPiTurnToWorkItemResult(
  result: PiResult<PiTurnResult>,
  runtime: "opencode" | "pi",
  model: string | null,
  variant: string | null,
): WorkItemResult {
  if (!result.ok) {
    const error = result.error
    const code = mapPiErrorKind(error.kind)
    const hint = error.kind === "missing-session" ? "reset" as const : undefined
    const output = buildAgentJobOutput(
      false,
      null,
      runtime,
      model,
      variant,
      null,
      error.message,
      result.diagnostics,
      hint,
    )
    return {
      status: "failed",
      message: error.message,
      error: { code, message: error.message },
      output,
      exitCode: 1,
    }
  }
  const facts = result.value.facts
  const output = buildAgentJobOutput(
    true,
    facts.runtimeSessionId,
    runtime,
    model,
    variant,
    facts.finalAssistantText,
    null,
    result.value.diagnostics,
  )
  return {
    status: "completed",
    message: "AgentJob completed",
    output,
    exitCode: 0,
  }
}

function mapPiErrorKind(kind: string): string {
  if (kind === "deadline-exceeded") return "timeout"
  if (kind === "missing-session") return "runtime-session-missing"
  return kind
}
