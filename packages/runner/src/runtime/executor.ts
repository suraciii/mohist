import { isAbsolute, join, relative, resolve } from 'node:path'
import type { ActionError, ActionResult, JsonObject, DispatchWorkItem, WorkItemResult } from '../core/types.js'
import { isObject, stringInput } from '../core/json.js'
import { errorMessage } from '../core/errors.js'
import { stringAt } from '../core/json-path.js'
import { renderTemplate, unresolvedReferences } from '../core/template.js'
import { ensureDir } from '../system/process.js'
import { WorkspaceManager, WorkspaceNetworkTimeoutError } from './workspace.js'
import type { NamedWorkspaceManager } from './workspace-entity.js'
import type { ActionRegistry } from '../actions/registry.js'
import type { ServerConnection } from '../server/connection.js'
import type { AgentSessionRuntimeEventOutbox } from '../server/runtime-event-outbox.js'
import type { OpenCodeRuntime } from './opencode/index.js'
import type { PiRuntime } from './pi/index.js'
import { captureAndUploadArtifactsForWork } from './artifact-side-effects.js'
import { applySetVarsForWork } from './set-vars-apply.js'
import { captureOutputs } from './output-capture.js'
import { checkBranchStability, expectedWorkspaceBranch } from './branch-stability.js'
import { executeCheckDispatch, type CheckDeclaration } from './check-execution.js'
import { tryRecovery } from './recovery.js'
import { createCredentialMaskerFromEnvironment, TaskLogCollector, TaskLogger } from './task-log.js'
import { isActionFailure } from '../actions/action-result.js'
import type { ActionCapabilitySet } from '../actions/manifest.js'
import { validateActionInput, deferredInputFields, injectEngineInputs } from '../actions/input-validation.js'
import {
  malformedToUnexpectedError,
  normalizeActionResult,
  passThroughExitCode,
  passThroughOutcome,
  passThroughTurnFact,
} from '../actions/result-validation.js'
import { evaluateCompletion, promiseValue, type CompletionEvaluation } from '../actions/expectations.js'
import type { AgentJobExecutor } from './agent-job-executor.js'
import type { ActionEffects } from '../actions/host.js'
import { capabilitySet } from '../actions/host.js'
import type { BindingRecoveryCoordinator } from './binding-recovery.js'
import { SkillResolver } from './skill-resolver.js'
import { renderWithDeferred, buildActionHost, type ExecutorCapabilityDeps } from './executor-capabilities.js'
import {
  applyBoundaryOutcome,
  buildCompletionBoundary,
  buildExecutionIdentity,
  isWorkflowTask,
  probeCommitReceipt,
  type ActionExecutionCapture,
  type CompletionWorkspace,
} from './completion-boundary.js'
import type { WorkflowTaskCompletionBoundary } from '../core/types.js'

const COMPLETED_STATUSES = new Set(['completed', 'success', 'succeeded', 'pass', 'passed'])
const CHECK_STATUS_BY_ACTION_STATUS = new Map([
  ['pass', 'pass'],
  ['passed', 'pass'],
  ['success', 'pass'],
  ['succeeded', 'pass'],
  ['completed', 'pass'],
  ['pending', 'pending'],
])
const CHECK_WORK_TYPES = new Set(['check', 'checks'])

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
    private piRuntime: PiRuntime | null = null,
    private readonly bindingRecoveryCoordinator: BindingRecoveryCoordinator | null = null,
    private readonly skillResolver: SkillResolver = new SkillResolver(),
    private readonly namedWorkspaceManager: NamedWorkspaceManager | null = null,
    private readonly runnerId = 'unknown',
  ) {}

  updateOpenCodeRuntime(runtime: OpenCodeRuntime | null) {
    this.openCodeRuntime = runtime
  }

  updateRuntimeEventOutbox(outbox: AgentSessionRuntimeEventOutbox | null) {
    this.agentSessionRuntimeEventOutbox = outbox
  }

  async execute(work: DispatchWorkItem, signal: AbortSignal): Promise<WorkItemResult> {
    return this.executeWithLog(work, signal, null).then((exec) => exec.result)
  }

  async executeWithLog(
    work: DispatchWorkItem,
    signal: AbortSignal,
    collector: TaskLogCollector | null,
  ): Promise<WorkExecution> {
    const ownedCollector = collector ?? new TaskLogCollector({ now: this.now })
    const logger = new TaskLogger({ collector: ownedCollector, masker: createCredentialMaskerFromEnvironment() })
    if (isWorkflowTask(work)) {
      return await this.executeWorkflowTaskWithBoundary(work, signal, ownedCollector, logger)
    }

    let resolvedWorkspace: ResolvedWorkspace
    if (work.ownerKind !== 'agent-job') {
      const precheck = await this.prepareWorkspace(work, signal, logger)
      if (precheck.kind === 'failure') return { result: precheck.result, collector: ownedCollector }
      resolvedWorkspace = precheck.workspace
    } else {
      resolvedWorkspace = this.workspaceFromVariables(work)
    }

    if (work.workType === 'checks') {
      const result = await this.executeChecks(work, resolvedWorkspace, signal, logger)
      return { result, collector: ownedCollector }
    }
    const result = await this.executeOne(work, resolvedWorkspace, signal, logger, {
      actionStarted: false,
      actionResult: null,
      phase: 'execution',
    })
    return { result, collector: ownedCollector }
  }

  private async executeWorkflowTaskWithBoundary(
    work: DispatchWorkItem,
    signal: AbortSignal,
    collector: TaskLogCollector,
    log: TaskLogger,
  ): Promise<WorkExecution> {
    const capture: ActionExecutionCapture = { actionStarted: false, actionResult: null, phase: 'admission' }
    let resolvedWorkspace: ResolvedWorkspace | null = null
    let result: WorkItemResult
    try {
      const precheck = await this.prepareWorkspace(work, signal, log)
      if (precheck.kind === 'failure') {
        capture.phase = 'workspace-setup'
        result = precheck.result
      } else {
        resolvedWorkspace = precheck.workspace
        result = await this.executeOne(work, resolvedWorkspace, signal, log, capture)
      }
    } catch (error) {
      capture.phase = 'outer-catch'
      result = failure(work, errorMessage(error))
    }

    const workspace = resolvedWorkspace as CompletionWorkspace | null
    const identity = buildExecutionIdentity(work, workspace, this.runnerId)
    const expectedVariables = work.variables ?? {}
    const expectedWorkspace = workspaceVariables(work)
    const receipt = await probeCommitReceipt({
      work,
      identity,
      workspace,
      expectedBranch: workspace?.branch ?? expectedWorkspaceBranch(expectedVariables),
      expectedHead: work.workspaceHead ?? stringField(expectedWorkspace, 'head'),
      expectedTree: work.workspaceTree ?? stringField(expectedWorkspace, 'tree'),
      signal,
      now: this.now,
      log,
    })
    const boundaryReceipt = capture.actionStarted
      ? receipt
      : {
          ...receipt,
          authoritative: false,
          reason: receipt.reason ?? `action-not-started:${capture.phase}`,
        }
    const boundary = buildCompletionBoundary({
      work,
      runnerId: this.runnerId,
      workspace,
      capture,
      result,
      receipt: boundaryReceipt,
      now: this.now,
    })
    return {
      result: applyBoundaryOutcome(result, boundary),
      boundary,
      collector,
    }
  }

  private async prepareWorkspace(
    work: DispatchWorkItem,
    signal: AbortSignal,
    log: TaskLogger,
  ): Promise<{ kind: 'ok'; workspace: ResolvedWorkspace } | { kind: 'failure'; result: WorkItemResult }> {
    try {
      const wsName = readWorkspaceName(work)
      if (wsName && this.namedWorkspaceManager && work.projectId) {
        const gitUrl = stringInput(work.variables ?? {}, 'repository.gitUrl')
        const baseBranch = stringInput(work.variables ?? {}, 'repository.baseBranch')
        if (gitUrl && baseBranch) {
          const info = await this.namedWorkspaceManager.materializeForIssue(
            work.projectId,
            wsName,
            gitUrl,
            baseBranch,
            signal,
          )
          return { kind: 'ok', workspace: { path: info.path, branch: `mohist/ws-${wsName}` } }
        }
      }
      const info = await this.workspaceManager.prepare(work, signal, log)
      return { kind: 'ok', workspace: infoToResolved(info) }
    } catch (error) {
      return { kind: 'failure', result: workspaceSetupFailure(work, error) }
    }
  }

  private async executeOne(
    work: DispatchWorkItem,
    resolvedWorkspace: ResolvedWorkspace,
    signal: AbortSignal,
    log: TaskLogger,
    capture: ActionExecutionCapture,
  ): Promise<WorkItemResult> {
    if (work.ownerKind === 'agent-job') {
      if (!this.agentJobExecutor) {
        return failure(work, 'AgentJob dispatch received without an AgentJobExecutor wired on the WorkExecutor')
      }
      try {
        capture.phase = 'agent-job'
        return await this.agentJobExecutor.execute(work, signal)
      } catch (error) {
        return failure(work, errorMessage(error))
      }
    }

    capture.phase = 'action-resolution'
    const resolved = this.actions.resolve(work.uses)
    if (resolved.kind === 'unknown') {
      return failure(work, `No action found for '${work.uses ?? ''}'`)
    }
    if (resolved.kind === 'tombstone') {
      return failure(work, removedActionMessage(work.uses ?? resolved.canonicalName, resolved.tombstone))
    }
    const definition = resolved.definition

    try {
      capture.phase = 'input-resolution'
      const variables = await this.variables(work, resolvedWorkspace, signal)
      const deferred = deferredInputFields(definition.manifest)
      const clonedWith = work.with ? structuredClone(work.with) : null
      const actionWith = injectEngineInputs(definition.manifest, clonedWith, variables)
      const unresolved = [
        ...unresolvedReferences(removeDeferredFields(actionWith, deferred), variables),
        ...unresolvedReferences(work.expect, variables),
      ]
      if (unresolved.length > 0) {
        return failure(work, formatUnresolvedError(work, unresolved))
      }

      const renderedWith = renderWithDeferred(actionWith, variables, deferred)
      const validation = validateActionInput(definition.manifest, renderedWith)
      if (validation.kind === 'failure') {
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
      capture.phase = 'start-branch-probe'
      const startCheck = await checkBranchStability(work, workDir, expectedBranch, 'start', signal, log)
      if (startCheck.kind === 'violation') {
        return startCheck.result
      }
      const caps = capabilitySet(definition.manifest)
      const host = buildActionHost(this.capabilityDeps(), work, workDir, signal, log, caps)
      let rawResult: unknown
      capture.actionStarted = true
      capture.phase = 'action'
      try {
        rawResult = await definition.run(validatedWith, host)
      } catch (thrown) {
        rawResult = malformedToUnexpectedError(
          `Action '${definition.manifest.name}' threw before returning a result: ${errorMessage(thrown)}`,
        )
      }
      const turnFact = (passThroughTurnFact(rawResult) ?? null) as { finalAssistantText?: string | null } | null
      const outcome = passThroughOutcome(rawResult)
      const normalized = normalizeActionResult(rawResult, definition.manifest, caps)
      let effects: ActionEffects = {}
      let validatedResult: ActionResult
      if (normalized.kind === 'malformed') {
        validatedResult = malformedToUnexpectedError(normalized.message) as ActionResult
      } else if (normalized.kind === 'ok') {
        validatedResult = { output: normalized.output } as ActionResult
        effects = normalized.effects
      } else {
        validatedResult = { error: normalized.error } as ActionResult
      }
      const exitCode = passThroughExitCode(rawResult)
      if (exitCode !== undefined && exitCode !== null) {
        ;(validatedResult as { exitCode?: number | null }).exitCode = exitCode
      }
      if (outcome) {
        ;(validatedResult as { outcome?: 'unknown' }).outcome = outcome
      }
      capture.actionResult = validatedResult
      const actionSucceeded = !isActionFailure(validatedResult)
      const completion = actionSucceeded
        ? await evaluateCompletion(renderedExpect, workDir, turnFact?.finalAssistantText ?? null)
        : null
      const projected = projectTaskOutput(work, validatedResult, completion, caps)
      const resultForRecovery = projected
      if (isActionFailure(validatedResult)) {
        if (resultForRecovery.status === 'unknown') return resultForRecovery
        const recoveryResult = tryRecovery(work, resultForRecovery, variables)
        if (recoveryResult) return recoveryResult
        return resultForRecovery
      }
      capture.phase = 'end-branch-probe'
      const endCheck = await checkBranchStability(work, workDir, expectedBranch, 'end', signal, log)
      if (endCheck.kind === 'violation') {
        return tryRecovery(work, endCheck.result, variables) ?? endCheck.result
      }
      capture.phase = 'artifact-capture'
      const artifactResult = await captureAndUploadArtifactsForWork(
        this.connection,
        work,
        workspaceRoot,
        workDir,
        resultForRecovery,
        validatedResult,
        variables,
        signal,
      )
      const recoveryResult = tryRecovery(work, artifactResult, variables)
      if (recoveryResult) return recoveryResult
      if (artifactResult.status !== 'completed') return artifactResult
      capture.phase = 'output-capture'
      const withCapturedOutputs = this.captureDeclaredOutputs(work, artifactResult, validatedResult)
      capture.phase = 'set-variable'
      const withVarsResult = await applySetVarsForWork(
        this.connection,
        work,
        withCapturedOutputs,
        signal,
        effects.writeVars ?? {},
      )
      if (withVarsResult.status !== 'completed') return withVarsResult
      return effects.addTasks && effects.addTasks.length > 0
        ? { ...withVarsResult, addTasks: effects.addTasks }
        : withVarsResult
    } catch (error) {
      return failure(work, errorMessage(error))
    }
  }

  private capabilityDeps(): ExecutorCapabilityDeps {
    return {
      connection: this.connection,
      skillResolver: this.skillResolver,
      piRuntime: this.piRuntime,
      openCodeRuntime: this.openCodeRuntime,
      agentSessionRuntimeEventOutbox: this.agentSessionRuntimeEventOutbox,
      runtimeEventRecordId: this.runtimeEventRecordId,
      bindingRecoveryCoordinator: this.bindingRecoveryCoordinator,
    }
  }

  private async executeChecks(
    work: DispatchWorkItem,
    resolvedWorkspace: ResolvedWorkspace,
    signal: AbortSignal,
    log: TaskLogger,
  ): Promise<WorkItemResult> {
    const variables = await this.variables(work, resolvedWorkspace, signal)
    const rawChecks: unknown[] = Array.isArray(work.with?.checks) ? work.with.checks : []
    const checks = rawChecks.filter(isCheck)
    const workspaceRoot = this.workspaceRoot(variables)
    const builder = this.buildCheckHost.bind(this)
    return await executeCheckDispatch(checks, variables, {
      actions: this.actions,
      buildHost: (
        checkWork: DispatchWorkItem,
        checkSignal: AbortSignal,
        checkWorkDir: string,
        caps: ActionCapabilitySet,
      ) => buildActionHost(this.capabilityDeps(), checkWork, checkWorkDir, checkSignal, log, caps),
      formatUnresolved: formatCheckUnresolvedError,
      resolveWorkDir: (withInput) => this.resolveWorkDir(withInput, workspaceRoot),
      toCheckStatus,
    })
  }

  private buildCheckHost(caps: ActionCapabilitySet) {
    return {}
  }

  private async variables(
    work: DispatchWorkItem,
    resolvedWorkspace: ResolvedWorkspace,
    signal: AbortSignal,
  ): Promise<JsonObject> {
    const workspace = resolvedWorkspaceToVariables(resolvedWorkspace)
    const source = work.variables ?? {}
    const roots = ['workflow', 'stage', 'work', 'issue', 'repository', 'workspace', 'vars', 'tasks', 'prompts']
    const variables: JsonObject = {}
    for (const root of roots) {
      if (root !== 'workspace' && source[root] !== undefined) variables[root] = source[root]
    }
    variables.workspace = workspace
    return variables
  }

  private workspaceFromVariables(work: DispatchWorkItem): ResolvedWorkspace {
    const variables = work.variables ?? {}
    const ws = variables['workspace']
    if (!isObject(ws)) {
      return { path: '', branch: null, workspaceId: work.workspaceId ?? null, workspaceGeneration: work.workspaceGeneration ?? null }
    }
    const name = stringField(ws, 'name')
    if (name) {
      return {
        path: name,
        branch: `mohist/ws-${name}`,
        workspaceId: work.workspaceId ?? stringField(ws, 'id') ?? stringField(ws, 'identity'),
        workspaceGeneration: work.workspaceGeneration ?? scalarWorkspaceField(ws, 'generation'),
      }
    }
    return {
      path: stringField(ws, 'path') ?? '',
      branch: stringField(ws, 'branch'),
      workspaceId: work.workspaceId ?? stringField(ws, 'id') ?? stringField(ws, 'identity'),
      workspaceGeneration: work.workspaceGeneration ?? scalarWorkspaceField(ws, 'generation'),
    }
  }

  private workspaceRoot(variables: JsonObject) {
    return stringAt(variables, ['workspace', 'path']) ?? join(this.fallbackWorkDir, 'default')
  }

  private async resolveWorkDir(withInput: JsonObject | null, workspaceRoot: string) {
    const requested = stringInput(withInput, 'working-directory')
    const root = resolve(workspaceRoot)
    const workDir = requested ? resolveWorkspacePath(root, requested) : root
    await ensureDir(workDir)
    return workDir
  }

  private captureDeclaredOutputs(
    work: DispatchWorkItem,
    result: WorkItemResult,
    actionResult: ActionResult,
  ): WorkItemResult {
    if (result.status !== 'completed') return result
    const capturedOutputs = captureOutputs(work.outputs, actionResult)
    return capturedOutputs ? { ...result, capturedOutputs } : result
  }
}

export function stripRunnerPrivateFacts(result: ActionResult): {
  publicActionResult: ActionResult
  turnFact: { finalAssistantText?: string | null } | null
} {
  if (!result || typeof result !== 'object' || !('turnFact' in result)) {
    return { publicActionResult: result, turnFact: null }
  }
  const turnFact = result.turnFact ?? null
  const { turnFact: _ignored, ...rest } = result
  return { publicActionResult: rest as ActionResult, turnFact }
}

function projectTaskOutput(
  work: DispatchWorkItem,
  result: ActionResult,
  completion: CompletionEvaluation | null,
  caps: ActionCapabilitySet,
): WorkItemResult {
  if (result.outcome === 'unknown') {
    return {
      status: 'unknown',
      message: isActionFailure(result) ? result.error.message : null,
      error: isActionFailure(result) ? result.error : null,
      exitCode: result.exitCode,
    }
  }
  if (isActionFailure(result)) {
    return {
      status: failureStatus(work),
      message: result.error.message,
      error: result.error,
      exitCode: result.exitCode,
    }
  }
  if (caps.has('agent-turn')) {
    if (completion === null) return { status: 'completed', output: null, exitCode: result.exitCode }
    const value = promiseValue(completion.matched ?? null)
    const projectedOutput: JsonObject | null = value !== null ? { promise: value } : null
    if (!completion.satisfied) {
      return {
        status: failureStatus(work),
        message: completion.message,
        error: { code: 'expectation-failed', message: completion.message },
        output: projectedOutput,
        exitCode: result.exitCode,
      }
    }
    return { status: 'completed', output: projectedOutput, exitCode: result.exitCode }
  }
  if (completion === null) return { status: 'completed', output: result.output, exitCode: result.exitCode }
  if (!completion.satisfied) {
    return {
      status: failureStatus(work),
      message: completion.message,
      error: { code: 'expectation-failed', message: completion.message },
      output: result.output,
      exitCode: result.exitCode,
    }
  }
  return { status: 'completed', output: result.output, exitCode: result.exitCode }
}

export interface WorkExecution {
  result: WorkItemResult
  collector: TaskLogCollector
  boundary?: WorkflowTaskCompletionBoundary
}

type ResolvedWorkspace = CompletionWorkspace

function infoToResolved(info: { path: string; branch?: string | null; workspaceId?: string | null; workspaceGeneration?: string | number | null }): ResolvedWorkspace {
  return {
    path: info.path,
    branch: info.branch ?? null,
    workspaceId: info.workspaceId ?? null,
    workspaceGeneration: info.workspaceGeneration ?? null,
  }
}

function resolvedWorkspaceToVariables(workspace: ResolvedWorkspace): JsonObject {
  return {
    path: workspace.path,
    branch: workspace.branch,
    ...(workspace.workspaceId ? { id: workspace.workspaceId, identity: workspace.workspaceId } : {}),
    ...(workspace.workspaceGeneration !== null && workspace.workspaceGeneration !== undefined
      ? { generation: workspace.workspaceGeneration }
      : {}),
  }
}

function readWorkspaceName(work: DispatchWorkItem): string | null {
  const variables = work.variables ?? {}
  const ws = variables['workspace']
  if (!isObject(ws)) return null
  const name = ws['name']
  return typeof name === 'string' && name.trim().length > 0 ? name.trim() : null
}

function defaultRuntimeEventRecordId(): string {
  return `evt_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 10)}`
}

function workspaceSetupFailure(work: DispatchWorkItem, error: unknown): WorkItemResult {
  const message = error instanceof Error ? error.message : String(error)
  const detail =
    error instanceof WorkspaceNetworkTimeoutError
      ? `workspace preparation timed out: ${message}`
      : `could not prepare workflow workspace: ${message}`
  const failureMessage = detail.slice(0, 4000)
  return {
    status: failureStatus(work),
    message: failureMessage,
    error: { code: 'workspace-setup', message: failureMessage },
  }
}

function failure(work: DispatchWorkItem, message: string): WorkItemResult {
  return { status: failureStatus(work), message, error: { code: 'runner-failed', message } }
}

function removeDeferredFields(withInput: JsonObject | null | undefined, deferred: Set<string>): JsonObject | null {
  if (!withInput) return null
  const immediate: JsonObject = {}
  for (const [key, value] of Object.entries(withInput)) {
    if (!deferred.has(key)) immediate[key] = value
  }
  return immediate
}

function failureStatus(work: DispatchWorkItem): 'fail' | 'failed' {
  return CHECK_WORK_TYPES.has(work.workType) ? 'fail' : 'failed'
}

function removedActionMessage(uses: string, tombstone: { name: string; guidance: string }): string {
  return `Workflow task uses the removed Action '${uses}'. ${tombstone.guidance}`
}

function validationFailureResult(work: DispatchWorkItem, error: ActionError): WorkItemResult {
  return {
    status: failureStatus(work),
    message: error.message,
    error,
  }
}

function toCheckStatus(status: string) {
  return CHECK_STATUS_BY_ACTION_STATUS.get(status.toLowerCase()) ?? 'fail'
}

function isCheck(value: unknown): value is CheckDeclaration {
  return isObject(value) && typeof value.uses === 'string'
}

function resolveWorkspacePath(workspaceRoot: string, requested: string) {
  const resolved = isAbsolute(requested) ? resolve(requested) : resolve(workspaceRoot, requested)
  const rel = relative(workspaceRoot, resolved)
  if (rel.startsWith('..') || isAbsolute(rel)) {
    throw new Error(`working-directory '${requested}' escapes workspace.path`)
  }
  return resolved
}

function formatUnresolvedError(work: DispatchWorkItem, unresolved: string[]): string {
  const label = work.title?.trim() || work.uses || work.workId
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(', ')
  return (
    'Task ' +
    work.workId +
    ' (' +
    label +
    ') references undefined variable(s): ' +
    refs +
    '. ' +
    'Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}.'
  )
}

function formatCheckUnresolvedError(unresolved: string[]): string {
  const refs = unresolved.map((p) => "'${{ " + p + " }}'").join(', ')
  return (
    'check references undefined variable(s): ' +
    refs +
    '. ' +
    'Add the variable to workflow.variables, define it in a parent stage, or escape the literal with \\${{ ... }}.'
  )
}

function stringField(obj: JsonObject, key: string): string | null {
  const value = obj[key]
  return typeof value === 'string' ? value : null
}

function scalarWorkspaceField(obj: JsonObject, key: string): string | number | null {
  const value = obj[key]
  return typeof value === 'string' || typeof value === 'number' ? value : null
}

function workspaceVariables(work: DispatchWorkItem): JsonObject {
  const value = work.variables?.workspace
  return isObject(value) ? value : {}
}
