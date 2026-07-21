import { isAbsolute, join, relative, resolve } from "node:path"
import type { ActionError, ActionResult, JsonObject, JsonValue, RenderedWorkItem, WorkItemResult } from "../core/types.js"
import { isObject, stringInput } from "../core/json.js"
import { errorMessage } from "../core/errors.js"
import { stringAt } from "../core/json-path.js"
import { renderTemplate, wholeStringUnresolvedReferences } from "../core/template.js"
import { ensureDir } from "../system/process.js"
import { runnerVariables, WorkspaceManager, WorkspaceNetworkTimeoutError } from "./workspace.js"
import type { ActionRegistry } from "../actions/registry.js"
import type { ServerConnection } from "../server/connection.js"
import type { AgentSessionRuntimeEventOutbox } from "../server/runtime-event-outbox.js"
import type { OpenCodeRuntime } from "./opencode/index.js"
import { captureAndUploadArtifactsForWork } from "./artifact-side-effects.js"
import { applySetVarsForWork } from "./set-vars-apply.js"
import { captureOutputs } from "./output-capture.js"
import { checkBranchStability, expectedWorkspaceBranch } from "./branch-stability.js"
import { executeCheckDispatch, type CheckDeclaration } from "./check-execution.js"
import { tryRecovery } from "./recovery.js"
import { enforceCleanWorktree, resolveCleanupAgentAction } from "./worktree-enforcement.js"
import { createCredentialMaskerFromEnvironment, TaskLogCollector, TaskLogger } from "./task-log.js"
import { fail as actionFail, isActionFailure, succeed as actionSucceed } from "../actions/action-result.js"
import type { ActionDefinition, ActionManifest, ActionCapabilitySet } from "../actions/manifest.js"
import { validateActionInput, deferredInputFields, injectEngineInputs } from "../actions/input-validation.js"
import { malformedToUnexpectedError, normalizeActionResult, passThroughExitCode, passThroughTurnFact } from "../actions/result-validation.js"
import {
  evaluateCompletion,
  promiseValue,
  type CompletionEvaluation,
} from "../actions/expectations.js"
import type { AgentJobExecutor } from "./agent-job-executor.js"
import type { ActionHost, ActionEffects, AgentTurnRequest } from "../actions/host.js"
import { capabilitySet } from "../actions/host.js"
import { composeOpencodePrompt, DEFAULT_TURN_DEADLINE_MS } from "../actions/opencode.js"
import { WorkflowAgentSessionReporter } from "../actions/workflow-agent-session-reporter.js"
import { parseModelIdentifier } from "./opencode/index.js"
import { resolveIssueFields, type IssueFields } from "../actions/issue-fields.js"
import { createHash } from "node:crypto"

const COMPLETED_STATUSES = new Set(["completed", "success", "succeeded", "pass", "passed"])
const CHECK_STATUS_BY_ACTION_STATUS = new Map([
  ["pass", "pass"],
  ["passed", "pass"],
  ["success", "pass"],
  ["succeeded", "pass"],
  ["completed", "pass"],
  ["pending", "pending"],
])
const CHECK_WORK_TYPES = new Set(["check", "checks"])

export class WorkExecutor {
  constructor(
    private readonly actions: ActionRegistry,
    private readonly workspaceManager: WorkspaceManager,
    private readonly connection: ServerConnection,
    private readonly fallbackWorkDir = process.cwd(),
    private readonly now: () => Date = () => new Date(),
    private openCodeRuntime: OpenCodeRuntime | null = null,
    private readonly agentJobExecutor: AgentJobExecutor | null = null,
    private agentSessionRuntimeEventOutbox: AgentSessionRuntimeEventOutbox | null = null,
    private readonly runtimeEventRecordId: () => string = defaultRuntimeEventRecordId,
  ) {}

  updateOpenCodeRuntime(runtime: OpenCodeRuntime | null) {
    this.openCodeRuntime = runtime
  }

  updateRuntimeEventOutbox(outbox: AgentSessionRuntimeEventOutbox | null) {
    this.agentSessionRuntimeEventOutbox = outbox
  }

  async execute(work: RenderedWorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    return this.executeWithLog(work, signal, null).then((exec) => exec.result)
  }

  async executeWithLog(work: RenderedWorkItem, signal: AbortSignal, collector: TaskLogCollector | null): Promise<WorkExecution> {
    const ownedCollector = collector ?? new TaskLogCollector({ now: this.now })
    const logger = new TaskLogger({ collector: ownedCollector, masker: createCredentialMaskerFromEnvironment() })
    let resolvedWorkspace: ResolvedWorkspace
    if (work.ownerKind !== "agent-job") {
      const precheck = await this.prepareWorkspace(work, signal, logger)
      if (precheck.kind === "failure") return { result: precheck.result, collector: ownedCollector }
      resolvedWorkspace = precheck.workspace
    } else {
      resolvedWorkspace = this.workspaceFromVariables(work)
    }

    if (work.workType === "checks") {
      const result = await this.executeChecks(work, resolvedWorkspace, signal, logger)
      return { result, collector: ownedCollector }
    }
    const result = await this.executeOne(work, resolvedWorkspace, signal, logger)
    return { result, collector: ownedCollector }
  }

  private async prepareWorkspace(work: RenderedWorkItem, signal: AbortSignal, log: TaskLogger): Promise<{ kind: "ok", workspace: ResolvedWorkspace } | { kind: "failure", result: WorkItemResult }> {
    try {
      const info = await this.workspaceManager.prepare(work, signal, log)
      return { kind: "ok", workspace: infoToResolved(info) }
    } catch (error) {
      return { kind: "failure", result: workspaceSetupFailure(work, error) }
    }
  }

  private async executeOne(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal, log: TaskLogger): Promise<WorkItemResult> {
    if (work.ownerKind === "agent-job") {
      if (!this.agentJobExecutor) {
        return failure(work, "AgentJob dispatch received without an AgentJobExecutor wired on the WorkExecutor")
      }
      try {
        return await this.agentJobExecutor.execute(work, signal)
      } catch (error) {
        return failure(work, errorMessage(error))
      }
    }

    const resolved = this.actions.resolve(work.uses)
    if (resolved.kind === "unknown") {
      return failure(work, `No action found for '${work.uses ?? ""}'`)
    }
    if (resolved.kind === "tombstone") {
      return failure(work, removedActionMessage(work.uses ?? resolved.canonicalName, resolved.tombstone))
    }
    const definition = resolved.definition

    try {
      const variables = await this.variables(work, resolvedWorkspace, signal)
      const deferred = deferredInputFields(definition.manifest)
      const actionWith = injectEngineInputs(definition.manifest, work.with, variables)
      const unresolved = [
        ...wholeStringUnresolvedReferences(removeDeferredFields(actionWith, deferred), variables),
        ...wholeStringUnresolvedReferences(work.expect, variables),
      ]
      if (unresolved.length > 0) {
        return failure(work, formatUnresolvedError(work, unresolved))
      }

      const renderedWith = this.renderWithDeferred(actionWith, variables, deferred)
      const validation = validateActionInput(definition.manifest, renderedWith)
      if (validation.kind === "failure") {
        const validationFailure = validationFailureResult(work, validation.error)
        const recoveryResult = tryRecovery(work, validationFailure, variables)
        if (recoveryResult) return recoveryResult
        return validationFailure
      }
      const validatedWith = validation.input
      const renderedExpect = work.expect != null ? renderTemplate(work.expect, variables) : null
      const workspaceRoot = this.workspaceRoot(variables)
      const workDir = await this.resolveWorkDir(renderedWith, workspaceRoot)
      const expectedBranch = expectedWorkspaceBranch(variables)
      const startCheck = await checkBranchStability(work, workDir, expectedBranch, "start", signal, log)
      if (startCheck.kind === "violation") {
        return startCheck.result
      }
      const caps = capabilitySet(definition.manifest)
      const host = this.buildActionHost(work, workDir, signal, log, caps)
      let rawResult: unknown
      try {
        rawResult = await definition.run(validatedWith, host)
      } catch (thrown) {
        rawResult = malformedToUnexpectedError(
          `Action '${definition.manifest.name}' threw before returning a result: ${errorMessage(thrown)}`,
        )
      }
      const turnFact = (passThroughTurnFact(rawResult) ?? null) as { finalAssistantText?: string | null } | null
      const normalized = normalizeActionResult(rawResult, definition.manifest, caps)
      let effects: ActionEffects = {}
      let validatedResult: ActionResult
      if (normalized.kind === "malformed") {
        validatedResult = malformedToUnexpectedError(normalized.message) as ActionResult
      } else if (normalized.kind === "ok") {
        validatedResult = { output: normalized.output } as ActionResult
        effects = normalized.effects
      } else {
        validatedResult = { error: normalized.error } as ActionResult
      }
      const exitCode = passThroughExitCode(rawResult)
      if (exitCode !== undefined && exitCode !== null) {
        ;(validatedResult as { exitCode?: number | null }).exitCode = exitCode
      }
      const actionSucceeded = !isActionFailure(validatedResult)
      const completion = actionSucceeded
        ? await evaluateCompletion(renderedExpect, workDir, turnFact?.finalAssistantText ?? null)
        : null
      const projected = projectTaskOutput(work, validatedResult, completion, caps)
      const resultForRecovery = projected
      const recoveryResult = tryRecovery(work, resultForRecovery, variables)
      if (recoveryResult) return recoveryResult
      if (resultForRecovery.status !== "completed") {
        return resultForRecovery
      }
      const endCheck = await checkBranchStability(work, workDir, expectedBranch, "end", signal, log)
      if (endCheck.kind === "violation") {
        return tryRecovery(work, endCheck.result, variables) ?? endCheck.result
      }
      const worktreeHostBuilder = (cleanupWork: RenderedWorkItem, cleanupSignal: AbortSignal, cleanupWorkDir: string) =>
        this.buildActionHost(cleanupWork, cleanupWorkDir, cleanupSignal, log, caps)
      const worktreeResult = await enforceCleanWorktree(
        work,
        workDir,
        resultForRecovery,
        renderedWith,
        variables,
        signal,
        resolveCleanupAgentAction((host, withInput) => definition.run(withInput, host)),
        { buildHost: worktreeHostBuilder },
        log,
      )
      if (worktreeResult.status !== "completed") {
        const recoveryResult = tryRecovery(work, worktreeResult, variables)
        if (recoveryResult) return recoveryResult
        return worktreeResult
      }
      const finalResult = await captureAndUploadArtifactsForWork(this.connection, work, workspaceRoot, workDir, worktreeResult, validatedResult, variables, signal)
      const withCapturedOutputs = this.captureDeclaredOutputs(work, finalResult, validatedResult)
      const withVarsResult = await applySetVarsForWork(this.connection, work, withCapturedOutputs, signal, effects.writeVars ?? {})
      if (withVarsResult.status !== "completed") return withVarsResult
      return effects.addTasks && effects.addTasks.length > 0
        ? { ...withVarsResult, addTasks: effects.addTasks }
        : withVarsResult
    } catch (error) {
      return failure(work, errorMessage(error))
    }
  }

  private renderWithDeferred(
    withInput: JsonObject | null | undefined,
    variables: JsonObject,
    deferred: Set<string>,
  ): JsonObject | null {
    if (!withInput) return null
    if (deferred.size === 0) return renderTemplate(withInput, variables)
    const rendered: JsonObject = {}
    for (const [key, value] of Object.entries(withInput)) {
      if (!deferred.has(key)) {
        rendered[key] = renderTemplate(value as JsonObject | null, variables)
      } else {
        rendered[key] = value
      }
    }
    return rendered
  }

  private buildActionHost(
    work: RenderedWorkItem,
    workDir: string,
    signal: AbortSignal,
    log: TaskLogger,
    caps: ActionCapabilitySet,
  ): ActionHost {
    const host: ActionHost = {
      workDir,
      signal,
      log,
      exec: async (command, args) => {
        const { runCommand } = await import("../system/process.js")
        const result = await runCommand(command, args?.map(String) ?? [], workDir, signal)
        return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr }
      },
    }

    if (caps.has("agent-turn")) {
      host.agent = this.buildAgentTurnCapability(work, workDir, signal)
    }

    if (caps.has("issue-fields")) {
      host.issue = this.buildIssueFieldsCapability(work, workDir, signal)
    }

    if (caps.has("workflow-checkpoint")) {
      host.checkpoint = this.buildCheckpointCapability(work)
    }

    return host
  }

  private buildAgentTurnCapability(work: RenderedWorkItem, workDir: string, signal: AbortSignal) {
    const self = this
    return {
      async turn(request: AgentTurnRequest): Promise<ActionResult> {
        const prompt = composeOpencodePrompt(request.prompt, work.parentIssueContext)

        const runtime = self.openCodeRuntime
        if (!runtime) {
          return actionFail("runtime-unavailable", "agent-turn requires the OpenCode runtime; the runner has not yet established the runtime or it is rebuilding")
        }
        if (!runtime.ready()) {
          const diagnostic = runtime.diagnostic()
          return actionFail("runtime-unavailable", `agent-turn requires the OpenCode runtime to be ready: ${diagnostic?.message ?? "no readiness diagnostic"}`)
        }

        const sessionName = request.session ?? work.workId
        let binding: { runtimeSessionId: string | null; workDir: string } | null = null
        if (self.connection && work.projectId) {
          try {
            const opened = await self.connection.openWorkflowAgentSession(
              work.projectId,
              work.workflowRunId,
              sessionName,
              {
                workId: work.workId,
                workType: work.workType,
                stage: work.stage,
                title: work.title,
                issueNumber: work.issueNumber,
                epicNumber: work.epicNumber,
                workDir,
              },
              signal,
            )
            if (opened.workDir && opened.workDir !== workDir) {
              return actionFail("session-workspace-mismatch", "Workflow AgentSession is bound to a different workspace; rerun the stage with a new task attempt before retrying")
            }
            binding = {
              runtimeSessionId: opened.runtimeSessionId ?? null,
              workDir: opened.workDir || "",
            }
          } catch (error) {
            return actionFail("session-binding-failed", `Failed to resolve the Workflow AgentSession binding: ${error instanceof Error ? error.message : String(error)}`)
          }
        }
        if (!binding) {
          binding = { runtimeSessionId: null, workDir }
        }

        if (binding.runtimeSessionId === null && sessionName && self.connection && work.projectId) {
          const modelResult = request.options?.model ? parseModelIdentifier(request.options.model) : null
          const model = modelResult?.kind === "ok" ? { providerID: modelResult.value.providerID, modelID: modelResult.value.modelID } : null
          const created = await runtime.createSession({
            target: { runtime: "opencode", runtimeSessionId: null, workDir: binding.workDir },
            model,
          })
          if (!created.ok) {
            const kind = created.error.kind
            const code = kind === "deadline-exceeded" ? "timeout" : kind === "missing-session" ? "runtime-session-missing" : kind
            return actionFail(code, created.error.message, { exitCode: 1, turnFact: { finalAssistantText: null } })
          }
          try {
            await self.connection.attachWorkflowAgentSession(
              work.projectId,
              work.workflowRunId,
              sessionName,
              {
                runtimeSessionId: created.value.runtimeSessionId,
                workDir: created.value.workDir,
                processPid: null,
                model: request.options?.model ?? null,
                workId: work.workId,
              },
              signal,
            )
          } catch (error) {
            return actionFail("session-binding-failed", `Failed to persist the Workflow AgentSession binding: ${error instanceof Error ? error.message : String(error)}`, { exitCode: 1, turnFact: { finalAssistantText: null } })
          }
          binding = {
            runtimeSessionId: created.value.runtimeSessionId,
            workDir: created.value.workDir,
          }
        }

        const deadlineMs = request.deadlineMs ?? DEFAULT_TURN_DEADLINE_MS
        const modelOptions = request.options?.model ? parseModelIdentifier(request.options.model) : null
        const runtimeRequest = {
          target: {
            runtime: "opencode" as const,
            runtimeSessionId: binding.runtimeSessionId,
            workDir: binding.workDir,
          },
          prompt,
          deadlineMs,
          options: {
            model: modelOptions?.kind === "ok" ? { providerID: modelOptions.value.providerID, modelID: modelOptions.value.modelID } : null,
            variant: request.options?.variant ?? null,
            unknownKeys: undefined as readonly string[] | undefined,
          },
        }

        const reporter = createWorkflowReporter(
          work.projectId ?? null,
          work.workflowRunId,
          sessionName,
          { workId: work.workId, workType: work.workType, stage: work.stage ?? null },
          binding.runtimeSessionId,
          self.agentSessionRuntimeEventOutbox,
          self.runtimeEventRecordId,
        )

        if (reporter && binding.runtimeSessionId) {
          try {
            await reporter.awaitInput(prompt, binding.runtimeSessionId)
          } catch (error) {
            return actionFail("execution-unavailable", `failed to durably enqueue the Workflow AgentSession input: ${error instanceof Error ? error.message : String(error)}`)
          }
        }

        const observer = createWorkflowObserver(reporter)
        const result = await runtime.runTurn(runtimeRequest, signal, observer)

        enqueueTerminalClose(reporter, result, binding.runtimeSessionId)
        await reporter?.settle()

        if (!result.ok) {
          const kind = result.error.kind
          const code = kind === "deadline-exceeded" ? "timeout" : kind === "missing-session" ? "runtime-session-missing" : kind
          return actionFail(code, result.error.message, { exitCode: 1, turnFact: { finalAssistantText: null } })
        }

        const facts = result.value.facts
        const output: JsonObject = {
          kind: "opencode",
          status: "success",
          runtimeSessionId: facts.runtimeSessionId,
          model: request.options?.model ?? null,
          variant: request.options?.variant ?? null,
          text: facts.finalAssistantText,
          diagnostics: result.value.diagnostics.map((d) => ({ code: d.code, message: d.message })),
        }
        return actionSucceed(output, { exitCode: 0, turnFact: { finalAssistantText: facts.finalAssistantText } })
      },
    }
  }

  private buildIssueFieldsCapability(work: RenderedWorkItem, workDir: string, signal: AbortSignal) {
    const issueNumber = typeof work.issueNumber === "number" && work.issueNumber > 0 ? work.issueNumber : null
    const projectId = work.projectId ?? null
    return {
      async fields(): Promise<IssueFields> {
        return resolveIssueFields({
          workDir,
          signal,
          issueNumber,
          projectId,
        } as any)
      },
    }
  }

  private buildCheckpointCapability(work: RenderedWorkItem) {
    return {
      async token(scope: string): Promise<string> {
        return `cp_${createHash("sha256").update(`${work.workflowRunId}\0${scope}`).digest("hex").slice(0, 32)}`
      },
    }
  }

  private async executeChecks(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal, log: TaskLogger): Promise<WorkItemResult> {
    const variables = await this.variables(work, resolvedWorkspace, signal)
    const rawChecks: unknown[] = Array.isArray(work.with?.checks) ? work.with.checks : []
    const checks = rawChecks.filter(isCheck)
    const workspaceRoot = this.workspaceRoot(variables)
    const builder = this.buildCheckHost.bind(this)
    return await executeCheckDispatch(checks, variables, {
      actions: this.actions,
      buildHost: (checkWork: RenderedWorkItem, checkSignal: AbortSignal, checkWorkDir: string, caps: ActionCapabilitySet) =>
        this.buildActionHost(checkWork, checkWorkDir, checkSignal, log, caps),
      formatUnresolved: formatCheckUnresolvedError,
      resolveWorkDir: (withInput) => this.resolveWorkDir(withInput, workspaceRoot),
      toCheckStatus,
    })
  }

  private buildCheckHost(caps: ActionCapabilitySet) {
    return {}
  }

  private async variables(work: RenderedWorkItem, resolvedWorkspace: ResolvedWorkspace, signal: AbortSignal): Promise<JsonObject> {
    const workspace = resolvedWorkspaceToVariables(resolvedWorkspace)
    const userVariables = work.variables ?? {}
    const userRunner = userVariables.runner
    const mergedRunner: JsonObject = { ...runnerVariables() }
    if (isObject(userRunner)) {
      Object.assign(mergedRunner, userRunner)
    }
    return { ...userVariables, runner: mergedRunner, workspace }
  }

  private workspaceFromVariables(work: RenderedWorkItem): ResolvedWorkspace {
    const variables = work.variables ?? {}
    const ws = variables["workspace"]
    if (!isObject(ws)) {
      return { path: "", branch: null, changeDir: null }
    }
    return {
      path: stringField(ws, "path") ?? "",
      branch: stringField(ws, "branch"),
      changeDir: stringField(ws, "changeDir"),
    }
  }

  private workspaceRoot(variables: JsonObject) {
    return stringAt(variables, ["workspace", "path"]) ?? join(this.fallbackWorkDir, "default")
  }

  private async resolveWorkDir(withInput: JsonObject | null, workspaceRoot: string) {
    const requested = stringInput(withInput, "working-directory")
    const root = resolve(workspaceRoot)
    const workDir = requested ? resolveWorkspacePath(root, requested) : root
    await ensureDir(workDir)
    return workDir
  }

  private captureDeclaredOutputs(work: RenderedWorkItem, result: WorkItemResult, actionResult: ActionResult): WorkItemResult {
    if (result.status !== "completed") return result
    const capturedOutputs = captureOutputs(work.outputs, actionResult)
    return capturedOutputs ? { ...result, capturedOutputs } : result
  }
}

export function stripRunnerPrivateFacts(result: ActionResult): {
  publicActionResult: ActionResult
  turnFact: { finalAssistantText?: string | null } | null
} {
  if (!result || typeof result !== "object" || !("turnFact" in result)) {
    return { publicActionResult: result, turnFact: null }
  }
  const turnFact = result.turnFact ?? null
  const { turnFact: _ignored, ...rest } = result
  return { publicActionResult: rest as ActionResult, turnFact }
}

function projectTaskOutput(
  work: RenderedWorkItem,
  result: ActionResult,
  completion: CompletionEvaluation | null,
  caps: ActionCapabilitySet,
): WorkItemResult {
  if (isActionFailure(result)) {
    return {
      status: failureStatus(work),
      message: result.error.message,
      error: result.error,
      exitCode: result.exitCode,
    }
  }
  if (caps.has("agent-turn")) {
    if (completion === null) return { status: "completed", output: null, exitCode: result.exitCode }
    const value = promiseValue(completion.matched ?? null)
    const projectedOutput: JsonObject | null = value !== null ? { promise: value } : null
    if (!completion.satisfied) {
      return {
        status: failureStatus(work),
        message: completion.message,
        error: { code: "expectation-failed", message: completion.message },
        output: projectedOutput,
        exitCode: result.exitCode,
      }
    }
    return { status: "completed", output: projectedOutput, exitCode: result.exitCode }
  }
  if (completion === null) return { status: "completed", output: result.output, exitCode: result.exitCode }
  if (!completion.satisfied) {
    return {
      status: failureStatus(work),
      message: completion.message,
      error: { code: "expectation-failed", message: completion.message },
      output: result.output,
      exitCode: result.exitCode,
    }
  }
  return { status: "completed", output: result.output, exitCode: result.exitCode }
}

export interface WorkExecution {
  result: WorkItemResult
  collector: TaskLogCollector
}

type ResolvedWorkspace = { path: string, branch: string | null, changeDir: string | null }

function infoToResolved(info: { path: string, branch?: string | null, changeDir?: string | null }): ResolvedWorkspace {
  return { path: info.path, branch: info.branch ?? null, changeDir: info.changeDir ?? null }
}

function resolvedWorkspaceToVariables(workspace: ResolvedWorkspace): JsonObject {
  return { path: workspace.path, branch: workspace.branch, changeDir: workspace.changeDir }
}

function defaultRuntimeEventRecordId(): string {
  return `evt_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`
}

function workspaceSetupFailure(work: RenderedWorkItem, error: unknown): WorkItemResult {
  const message = error instanceof Error ? error.message : String(error)
  const detail = error instanceof WorkspaceNetworkTimeoutError
    ? `workspace preparation timed out: ${message}`
    : `could not prepare workflow workspace: ${message}`
  const failureMessage = detail.slice(0, 4000)
  return {
    status: failureStatus(work),
    message: failureMessage,
    error: { code: "workspace-setup", message: failureMessage },
  }
}

function failure(work: RenderedWorkItem, message: string): WorkItemResult {
  return { status: failureStatus(work), message, error: { code: "runner-failed", message } }
}

function removeDeferredFields(withInput: JsonObject | null | undefined, deferred: Set<string>): JsonObject | null {
  if (!withInput) return null
  const immediate: JsonObject = {}
  for (const [key, value] of Object.entries(withInput)) {
    if (!deferred.has(key)) immediate[key] = value
  }
  return immediate
}

function failureStatus(work: RenderedWorkItem): "fail" | "failed" {
  return CHECK_WORK_TYPES.has(work.workType) ? "fail" : "failed"
}

function removedActionMessage(uses: string, tombstone: { name: string; guidance: string }): string {
  return `Workflow task uses the removed Action '${uses}'. ${tombstone.guidance}`
}

function validationFailureResult(work: RenderedWorkItem, error: ActionError): WorkItemResult {
  return {
    status: failureStatus(work),
    message: error.message,
    error,
  }
}

function toCheckStatus(status: string) {
  return CHECK_STATUS_BY_ACTION_STATUS.get(status.toLowerCase()) ?? "fail"
}

function isCheck(value: unknown): value is CheckDeclaration {
  return isObject(value) && typeof value.uses === "string"
}

function resolveWorkspacePath(workspaceRoot: string, requested: string) {
  const resolved = isAbsolute(requested) ? resolve(requested) : resolve(workspaceRoot, requested)
  const rel = relative(workspaceRoot, resolved)
  if (rel.startsWith("..") || isAbsolute(rel)) {
    throw new Error(`working-directory '${requested}' escapes workspace.path`)
  }
  return resolved
}

function formatUnresolvedError(work: RenderedWorkItem, unresolved: string[]): string {
  const label = work.title?.trim() || work.uses || work.workId
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(", ")
  return "Task " + work.workId + " (" + label + ") references undefined variable(s): " + refs + ". " +
    "Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}."
}

function formatCheckUnresolvedError(unresolved: string[]): string {
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(", ")
  return "check references undefined variable(s): " + refs + ". " +
    "Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}."
}

function stringField(obj: JsonObject, key: string): string | null {
  const value = obj[key]
  return typeof value === "string" ? value : null
}

function createWorkflowReporter(
  projectId: string | null,
  workflowRunId: string,
  sessionName: string,
  workMetadata: { workId: string; workType: string; stage: string | null },
  runtimeSessionId: string | null,
  outbox: AgentSessionRuntimeEventOutbox | null,
  runtimeEventRecordId: () => string,
): WorkflowAgentSessionReporter | null {
  if (!projectId) return null
  if (!outbox) return null
  if (!runtimeSessionId) return null
  return new WorkflowAgentSessionReporter({
    outbox,
    projectId,
    workflowRunId,
    sessionName,
    workMetadata,
    randomId: runtimeEventRecordId,
  })
}

function createWorkflowObserver(reporter: WorkflowAgentSessionReporter | null) {
  if (!reporter) return undefined
  return {
    onEvent: (event: any) => {
      reporter.registerEvent(event)
    },
  }
}

function enqueueTerminalClose(
  reporter: WorkflowAgentSessionReporter | null,
  result: any,
  runtimeSessionId: string | null,
): void {
  if (!reporter) return
  if (reporter.inputWasRejected()) return
  if (runtimeSessionId === null) return
  if (result.ok) {
    reporter.registerClose({ status: "completed", exitCode: 0, runtimeSessionId })
    return
  }
  reporter.registerClose({
    status: "failed",
    exitCode: 1,
    failureReason: result.error.message,
    runtimeSessionId,
  })
}
