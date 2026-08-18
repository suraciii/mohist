import { createHash } from 'node:crypto'
import type { ActionResult, JsonObject, DispatchWorkItem } from '../core/types.js'
import { errorMessage } from '../core/errors.js'
import { renderWithSkippedFields } from '../core/template.js'
import type { ServerConnection } from '../server/connection.js'
import type { AgentSessionRuntimeEventOutbox } from '../server/runtime-event-outbox.js'
import type { OpenCodeRuntime, RuntimeResult, RuntimeTurnResult } from './opencode/index.js'
import type { PiRuntime } from './pi/index.js'
import type { TaskLogger } from './task-log.js'
import { fail as actionFail, succeed as actionSucceed } from '../actions/action-result.js'
import type { ActionCapabilitySet } from '../actions/manifest.js'
import type { ActionHost, AgentTurnRequest } from '../actions/host.js'
import { composeOpencodePrompt, DEFAULT_TURN_DEADLINE_MS } from '../actions/opencode.js'
import { composePiPrompt, piAction } from '../actions/pi.js'
import { WorkflowAgentSessionReporter } from '../actions/workflow-agent-session-reporter.js'
import { hasUnconfirmedCleanup, parseModelIdentifier } from './opencode/index.js'
import { resolveOrRecoverBinding, type BindingRecoveryCoordinator } from './binding-recovery.js'
import { resolveIssueFields, type IssueFields } from '../actions/issue-fields.js'
import type { SkillResolver } from './skill-resolver.js'
import { buildExecutionEnvelope } from './execution-envelope.js'
import type { RuntimeTurnRegistry } from './runtime-turn-registry.js'
import { workKey } from './work-result-journal.js'

export interface ExecutorCapabilityDeps {
  readonly connection: ServerConnection
  readonly skillResolver: SkillResolver
  readonly piRuntime: PiRuntime | null
  readonly openCodeRuntime: OpenCodeRuntime | null
  readonly agentSessionRuntimeEventOutbox: AgentSessionRuntimeEventOutbox | null
  readonly runtimeEventRecordId: () => string
  readonly bindingRecoveryCoordinator: BindingRecoveryCoordinator | null
  readonly runtimeTurnRegistry?: RuntimeTurnRegistry | null
}

export function renderWithDeferred(
  withInput: JsonObject | null | undefined,
  variables: JsonObject,
  deferred: Set<string>,
): JsonObject | null {
  return renderWithSkippedFields(withInput, variables, deferred)
}

export function buildActionHost(
  deps: ExecutorCapabilityDeps,
  work: DispatchWorkItem,
  workDir: string,
  signal: AbortSignal,
  log: TaskLogger,
  caps: ActionCapabilitySet,
  cleanupAttempt?: number,
): ActionHost {
  const host: ActionHost = {
    workDir,
    signal,
    log,
    cleanupAttempt: cleanupAttempt ?? null,
    piRuntime: deps.piRuntime,
    skillResolver: deps.skillResolver,
    agentDefinition: work.agentDefinition,
    runtimeTurnRegistry: deps.runtimeTurnRegistry,
    runtimeTurnKey: workKey(work),
    exec: async (command, args) => {
      const { runCommand } = await import('../system/process.js')
      const result = await runCommand(command, args?.map(String) ?? [], workDir, signal, undefined, undefined)
      return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr }
    },
  }

  if (caps.has('agent-turn')) {
    host.agent = buildAgentTurnCapability(deps, work, workDir, signal, cleanupAttempt)
  }

  if (caps.has('issue-fields')) {
    host.issue = buildIssueFieldsCapability(work, workDir, signal)
  }

  if (caps.has('workflow-checkpoint')) {
    host.checkpoint = buildCheckpointCapability(work)
  }

  return host
}

function buildAgentTurnCapability(
  deps: ExecutorCapabilityDeps,
  work: DispatchWorkItem,
  workDir: string,
  signal: AbortSignal,
  cleanupAttempt?: number,
) {
  const self = deps
  return {
    async turn(request: AgentTurnRequest): Promise<ActionResult> {
      if (work.uses?.trim().toLowerCase() === 'mohist/pi') {
        return await runPiAgentTurn(self, work, workDir, signal, request, cleanupAttempt)
      }
      const definition = work.agentDefinition
      const skillNames = definition?.skills ?? []
      const resolvedSkills = await self.skillResolver.resolve(skillNames, workDir)
      if (!resolvedSkills.ok) return actionFail(resolvedSkills.code, resolvedSkills.message)
      const prompt = buildExecutionEnvelope(
        composeOpencodePrompt(request.prompt, work.parentIssueContext),
        definition?.instructions,
        resolvedSkills.skills,
      )
      const modelName = definition?.model ?? request.options?.model
      const variant = definition?.variant ?? request.options?.variant

      const runtime = self.openCodeRuntime
      const sessionName = request.session ?? work.workId
      let binding: {
        agentSessionId: string
        runtimeSessionId: string | null
        workDir: string
        runnerId: string
        runtime: 'opencode'
      } | null = null
      let freshRuntimeSessionRequired = false
      let freshBindingCreated = false
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
              runtime: 'opencode',
            },
            signal,
          )
          if (opened.workDir && opened.workDir !== workDir) {
            const mismatchReporter = createWorkflowReporter(
              work.projectId,
              work.workflowRunId,
              sessionName,
              {
                workId: work.workId,
                taskRunId: work.taskRunId ?? '',
                workType: work.workType,
                stage: work.stage ?? null,
              },
              self.connection.runnerId,
              opened.sessionId,
              opened.runtimeSessionId ?? null,
              self.agentSessionRuntimeEventOutbox,
              self.runtimeEventRecordId,
              cleanupAttempt,
            )
            return await workflowActionFailure(
              mismatchReporter,
              opened.runtimeSessionId ?? null,
              'session-workspace-mismatch',
              'Workflow AgentSession is bound to a different workspace; rerun the stage with a new task attempt before retrying',
            )
          }
          if (opened.runtimeSessionId && isUnsettledWorkflowSessionStatus(opened.status)) {
            return runtimeActionFailure(
              'session-binding-failed',
              `Workflow AgentSession is ${opened.status}; the previous Runtime Session has not reached a terminal state, so retry is fail-closed`,
            )
          }
          binding = {
            agentSessionId: opened.sessionId,
            runtimeSessionId: opened.runtimeSessionId ?? null,
            workDir: opened.workDir || workDir,
            runnerId: self.connection.runnerId,
            runtime: 'opencode',
          }
          freshRuntimeSessionRequired = opened.needsFreshRuntimeSession === true
        } catch (error) {
          return runtimeActionFailure(
            'session-binding-failed',
            `Failed to resolve the Workflow AgentSession binding: ${errorMessage(error)}`,
          )
        }
      }
      if (!binding) {
        binding = {
          agentSessionId: '',
          runtimeSessionId: null,
          workDir,
          runnerId: self.connection?.runnerId ?? '',
          runtime: 'opencode',
        }
      }

      let reporter = createWorkflowReporter(
        work.projectId ?? null,
        work.workflowRunId,
        sessionName,
        { workId: work.workId, taskRunId: work.taskRunId ?? '', workType: work.workType, stage: work.stage ?? null },
        binding.runnerId,
        binding.agentSessionId,
        binding.runtimeSessionId,
        self.agentSessionRuntimeEventOutbox,
        self.runtimeEventRecordId,
        cleanupAttempt,
      )

      if (!runtime) {
        return await workflowActionFailure(
          reporter,
          binding.runtimeSessionId,
          'runtime-unavailable',
          'agent-turn requires the OpenCode runtime; the runner has not yet established the runtime or it is rebuilding',
        )
      }
      if (!runtime.ready()) {
        const diagnostic = runtime.diagnostic()
        return await workflowActionFailure(
          reporter,
          binding.runtimeSessionId,
          'runtime-unavailable',
          `agent-turn requires the OpenCode runtime to be ready: ${diagnostic?.message ?? 'no readiness diagnostic'}`,
        )
      }

      if (binding.runtimeSessionId === null && sessionName && self.connection && work.projectId) {
        const modelResult = modelName ? parseModelIdentifier(modelName) : null
        const model =
          modelResult?.kind === 'ok'
            ? { providerID: modelResult.value.providerID, modelID: modelResult.value.modelID }
            : null
        let created: Awaited<ReturnType<OpenCodeRuntime['createSession']>>
        try {
          created = await runtime.createSession({
            target: { runtime: 'opencode', runtimeSessionId: null, workDir: binding.workDir },
            model,
          })
        } catch (error) {
          return runtimeActionFailure('turn-failed', `OpenCode session creation failed: ${errorMessage(error)}`)
        }
        if (!created.ok) {
          const kind = created.error.kind
          const code =
            kind === 'deadline-exceeded' ? 'timeout' : kind === 'missing-session' ? 'runtime-session-missing' : kind
          return runtimeActionFailure(code, created.error.message)
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
              model: modelName ?? null,
              workId: work.workId,
              runtime: 'opencode',
            },
            signal,
          )
        } catch (error) {
          const attachReporter = createWorkflowReporter(
            work.projectId,
            work.workflowRunId,
            sessionName,
            {
              workId: work.workId,
              taskRunId: work.taskRunId ?? '',
              workType: work.workType,
              stage: work.stage ?? null,
            },
            self.connection.runnerId,
            binding.agentSessionId,
            created.value.runtimeSessionId,
            self.agentSessionRuntimeEventOutbox,
            self.runtimeEventRecordId,
            cleanupAttempt,
          )
          return await workflowActionFailure(
            attachReporter,
            created.value.runtimeSessionId,
            'session-binding-failed',
            `Failed to persist the Workflow AgentSession binding: ${errorMessage(error)}`,
          )
        }
        binding = {
          agentSessionId: binding.agentSessionId,
          runtimeSessionId: created.value.runtimeSessionId,
          workDir: created.value.workDir,
          runnerId: self.connection.runnerId,
          runtime: 'opencode',
        }
        reporter = createWorkflowReporter(
          work.projectId ?? null,
          work.workflowRunId,
          sessionName,
          { workId: work.workId, taskRunId: work.taskRunId ?? '', workType: work.workType, stage: work.stage ?? null },
          binding.runnerId,
          binding.agentSessionId,
          binding.runtimeSessionId,
          self.agentSessionRuntimeEventOutbox,
          self.runtimeEventRecordId,
          cleanupAttempt,
        )
      }

      if (freshRuntimeSessionRequired && binding.runtimeSessionId && sessionName && self.connection && work.projectId) {
        const modelResult = modelName ? parseModelIdentifier(modelName) : null
        const model =
          modelResult?.kind === 'ok'
            ? { providerID: modelResult.value.providerID, modelID: modelResult.value.modelID }
            : null
        let created: Awaited<ReturnType<OpenCodeRuntime['createSession']>>
        try {
          created = await runtime.createSession({
            target: { runtime: 'opencode', runtimeSessionId: null, workDir: binding.workDir },
            model,
          })
        } catch (error) {
          return runtimeActionFailure('turn-failed', `OpenCode retry session creation failed: ${errorMessage(error)}`)
        }
        if (!created.ok) {
          const kind = created.error.kind
          const code =
            kind === 'deadline-exceeded' ? 'timeout' : kind === 'missing-session' ? 'runtime-session-missing' : kind
          return runtimeActionFailure(code, created.error.message)
        }

        try {
          await self.connection.resetWorkflowAgentSession(
            work.projectId,
            work.workflowRunId,
            sessionName,
            {
              expectedRunnerId: binding.runnerId,
              expectedRuntime: binding.runtime,
              expectedRuntimeSessionId: binding.runtimeSessionId,
              replacementRuntimeSessionId: created.value.runtimeSessionId,
              replacementRuntime: 'opencode',
            },
            signal,
          )
        } catch (error) {
          return runtimeActionFailure(
            'session-binding-failed',
            `Failed to persist the fresh Workflow AgentSession binding: ${errorMessage(error)}`,
          )
        }

        binding = {
          agentSessionId: binding.agentSessionId,
          runtimeSessionId: created.value.runtimeSessionId,
          workDir: created.value.workDir,
          runnerId: self.connection.runnerId,
          runtime: 'opencode',
        }
        freshRuntimeSessionRequired = false
        freshBindingCreated = true
        reporter = createWorkflowReporter(
          work.projectId ?? null,
          work.workflowRunId,
          sessionName,
          { workId: work.workId, taskRunId: work.taskRunId ?? '', workType: work.workType, stage: work.stage ?? null },
          binding.runnerId,
          binding.agentSessionId,
          binding.runtimeSessionId,
          self.agentSessionRuntimeEventOutbox,
          self.runtimeEventRecordId,
          cleanupAttempt,
        )
      }

      const deadlineMs = request.deadlineMs ?? DEFAULT_TURN_DEADLINE_MS
      const modelOptions = modelName ? parseModelIdentifier(modelName) : null
      if (
        !freshRuntimeSessionRequired &&
        !freshBindingCreated &&
        binding.runtimeSessionId &&
        self.connection &&
        work.projectId
      ) {
        const expected = binding
        let recovery: Awaited<ReturnType<typeof resolveOrRecoverBinding>>
        try {
          recovery = await resolveOrRecoverBinding({
            runnerId: self.connection.runnerId,
            expected,
            runtime: { kind: 'opencode', runtime },
            probe: async (candidate) => {
              const result = await runtime.resolveSession({
                target: {
                  runtime: 'opencode',
                  runtimeSessionId: candidate.runtimeSessionId,
                  workDir: candidate.workDir,
                },
              })
              if (result.ok) return { ok: true, activeTurn: result.value.activeTurn }
              return { ok: false, kind: result.error.kind, message: result.error.message }
            },
            replace: async (current, replacement) => {
              await self.connection!.recoverMissingWorkflowAgentSession(
                work.projectId!,
                work.workflowRunId,
                sessionName,
                {
                  expectedRunnerId: current.runnerId,
                  expectedRuntime: current.runtime,
                  expectedRuntimeSessionId: current.runtimeSessionId,
                  replacementRuntimeSessionId: replacement.runtimeSessionId,
                },
                signal,
              )
            },
            model:
              modelOptions?.kind === 'ok'
                ? { providerID: modelOptions.value.providerID, modelID: modelOptions.value.modelID }
                : null,
            rejectActiveTurn: true,
            recoveryKey: expected.runtimeSessionId!,
            coordinator: self.bindingRecoveryCoordinator ?? undefined,
          })
        } catch (error) {
          return await workflowActionFailure(
            reporter,
            binding.runtimeSessionId,
            'session-binding-failed',
            `Workflow AgentSession binding recovery failed: ${errorMessage(error)}`,
          )
        }
        if (!recovery.ok) {
          if (recovery.kind === 'active-turn') {
            return runtimeActionFailure('session-binding-failed', recovery.message)
          }
          return await workflowActionFailure(reporter, binding.runtimeSessionId, recovery.kind, recovery.message)
        }
        binding = {
          ...recovery.binding,
          agentSessionId: binding.agentSessionId,
          runtime: 'opencode',
        }
      }

      const selectedBinding = binding
      const runtimeRequest = {
        target: {
          runtime: 'opencode' as const,
          runtimeSessionId: selectedBinding.runtimeSessionId,
          workDir: selectedBinding.workDir,
        },
        prompt,
        deadlineMs,
        options: {
          model:
            modelOptions?.kind === 'ok'
              ? { providerID: modelOptions.value.providerID, modelID: modelOptions.value.modelID }
              : null,
          variant: variant ?? null,
          ...(resolvedSkills.skills.length > 0 ? { skills: resolvedSkills.skills } : {}),
          unknownKeys: undefined as readonly string[] | undefined,
        },
      }

      reporter = createWorkflowReporter(
        work.projectId ?? null,
        work.workflowRunId,
        sessionName,
        { workId: work.workId, taskRunId: work.taskRunId ?? '', workType: work.workType, stage: work.stage ?? null },
        selectedBinding.runnerId,
        selectedBinding.agentSessionId,
        selectedBinding.runtimeSessionId,
        self.agentSessionRuntimeEventOutbox,
        self.runtimeEventRecordId,
        cleanupAttempt,
      )

      if (reporter && selectedBinding.runtimeSessionId) {
        try {
          await reporter.awaitInput(prompt, selectedBinding.runtimeSessionId)
        } catch (error) {
          return await workflowActionFailure(
            reporter,
            selectedBinding.runtimeSessionId,
            'execution-unavailable',
            `failed to durably enqueue the Workflow AgentSession input: ${errorMessage(error)}`,
          )
        }
      }

      self.runtimeTurnRegistry?.register(workKey(work), {
        agentSessionId: selectedBinding.agentSessionId,
        agentTurnId: reporter?.getAgentTurnId() ?? work.initialTurnId ?? null,
        runtime: 'opencode',
        runtimeSessionId: selectedBinding.runtimeSessionId,
        workDir: selectedBinding.workDir,
      })

      const observer = createWorkflowObserver(reporter)
      let result: RuntimeResult<RuntimeTurnResult>
      try {
        result = await runtime.runTurn(runtimeRequest, signal, observer)
      } catch (error) {
        result = failedRuntimeTurn(`OpenCode turn failed: ${errorMessage(error)}`)
      }

      enqueueTerminalClose(reporter, result, selectedBinding.runtimeSessionId)
      await reporter?.settle()

      if (!result.ok) {
        const kind = result.error.kind
        const code =
          kind === 'deadline-exceeded' ? 'timeout' : kind === 'missing-session' ? 'runtime-session-missing' : kind
        return runtimeActionFailure(
          code,
          result.error.message,
          hasUnconfirmedCleanup(result.diagnostics ?? result.error.diagnostics ?? []) ? 'unknown' : undefined,
        )
      }

      const facts = result.value.facts
      const output: JsonObject = {
        kind: 'opencode',
        status: 'success',
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

async function runPiAgentTurn(
  deps: ExecutorCapabilityDeps,
  work: DispatchWorkItem,
  workDir: string,
  signal: AbortSignal,
  request: AgentTurnRequest,
  cleanupAttempt?: number,
): Promise<ActionResult> {
  return await piAction({
    workflowRunId: work.workflowRunId,
    workId: work.workId,
    taskRunId: work.taskRunId ?? null,
    workType: work.workType,
    stage: work.stage,
    title: work.title,
    workDir,
    signal,
    projectId: work.projectId,
    issueNumber: work.issueNumber,
    epicNumber: work.epicNumber,
    parentIssueContext: work.parentIssueContext,
    piRuntime: deps.piRuntime,
    skillResolver: deps.skillResolver,
    agentDefinition: work.agentDefinition,
    serverConnection: deps.connection,
    runtimeEventOutbox: deps.agentSessionRuntimeEventOutbox,
    runtimeEventRecordId: deps.runtimeEventRecordId,
    runnerId: deps.connection.runnerId,
    runtimeTurnRegistry: deps.runtimeTurnRegistry,
    runtimeTurnKey: workKey(work),
    cleanupAttempt,
    agentRecovery: work.agentRecovery ?? null,
    preparedPrompt: composePiPrompt(request.prompt, work.parentIssueContext),
    preparedOptions: request.options,
  })
}

function buildIssueFieldsCapability(work: DispatchWorkItem, workDir: string, signal: AbortSignal) {
  const issueNumber = typeof work.issueNumber === 'number' && work.issueNumber > 0 ? work.issueNumber : null
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

function buildCheckpointCapability(work: DispatchWorkItem) {
  return {
    async token(scope: string): Promise<string> {
      return `cp_${createHash('sha256').update(`${work.workflowRunId}\0${scope}`).digest('hex').slice(0, 32)}`
    },
  }
}

function isUnsettledWorkflowSessionStatus(status: string | null | undefined): status is 'active' | 'unknown' {
  return status === 'active' || status === 'unknown'
}

function runtimeActionFailure(code: string, message: string, outcome?: 'unknown'): ActionResult {
  return actionFail(code, message, { exitCode: 1, outcome, turnFact: { finalAssistantText: null } })
}

async function workflowActionFailure(
  reporter: WorkflowAgentSessionReporter | null,
  runtimeSessionId: string | null,
  code: string,
  message: string,
): Promise<ActionResult> {
  enqueueTerminalClose(reporter, failedRuntimeTurn(message), runtimeSessionId)
  await reporter?.settle()
  return runtimeActionFailure(code, message)
}

function failedRuntimeTurn(message: string): RuntimeResult<RuntimeTurnResult> {
  const error = { kind: 'turn-failed' as const, message, diagnostics: [] }
  return { ok: false, error, diagnostics: [] }
}

function createWorkflowReporter(
  projectId: string | null,
  workflowRunId: string,
  sessionName: string,
  workMetadata: { workId: string; taskRunId: string; workType: string; stage: string | null },
  runnerId: string,
  agentSessionId: string,
  runtimeSessionId: string | null,
  outbox: AgentSessionRuntimeEventOutbox | null,
  runtimeEventRecordId: () => string,
  cleanupAttempt?: number,
): WorkflowAgentSessionReporter | null {
  if (!projectId) return null
  if (!outbox) return null
  if (!runtimeSessionId) return null
  if (!agentSessionId) return null
  return new WorkflowAgentSessionReporter({
    outbox,
    projectId,
    workflowRunId,
    sessionName,
    workMetadata: { ...workMetadata, runnerId, agentSessionId },
    runtime: 'opencode',
    randomId: runtimeEventRecordId,
    cleanupAttempt,
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
    reporter.registerClose({ status: 'completed', exitCode: 0, runtimeSessionId })
    return
  }
  reporter.registerClose({
    status: hasUnconfirmedCleanup(result.diagnostics ?? result.error?.diagnostics ?? []) ? 'unknown' : 'failed',
    exitCode: 1,
    failureReason: result.error.message,
    runtimeSessionId,
  })
}
