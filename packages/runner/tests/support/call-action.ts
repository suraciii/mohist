import type { ActionResult, JsonObject } from "../../src/core/types.js"
import type { ActionTestContext as ActionContext } from "./action-test-context.js"
import type { ActionHost, AgentTurn, AgentTurnRequest, IssueFieldsHost } from "../../src/actions/host.js"
import { resolveIssueFields } from "../../src/actions/issue-fields.js"
import { composeOpencodePrompt, DEFAULT_TURN_DEADLINE_MS } from "../../src/actions/opencode.js"
import { parseModelIdentifier } from "../../src/runtime/opencode/index.js"
import { WorkflowAgentSessionReporter } from "../../src/actions/workflow-agent-session-reporter.js"
import { fail as actionFail, succeed as actionSucceed } from "../../src/actions/action-result.js"
import { actionErrorMessage } from "../../src/actions/action-result.js"

export function hostFromActionContext(context: ActionContext): ActionHost {
  const issueNumber = typeof context.issueNumber === "number" && context.issueNumber > 0 ? context.issueNumber : null
  const projectId = context.projectId ?? null

  const issue: IssueFieldsHost | undefined = issueNumber !== null && projectId ? {
    async fields() {
      return resolveIssueFields({
        workDir: context.workDir,
        signal: context.signal,
        issueNumber,
        projectId,
      } as any)
    },
  } : undefined

  return {
    workDir: context.workDir,
    signal: context.signal,
    log: context.log ?? null,
    exec: async () => ({ exitCode: 0, stdout: "", stderr: "" }),
    issue,
    checkpoint: context.workflowRunId ? {
      async token(_scope: string) {
        return context.workflowRunId
      },
    } : undefined,
  }
}

function runtimeModel(options: { model?: string; variant?: string } | undefined) {
  if (!options?.model) return null
  const parsed = parseModelIdentifier(options.model)
  if (parsed.kind !== "ok") return null
  return { providerID: parsed.value.providerID, modelID: parsed.value.modelID }
}

function runtimeErrorCode(kind: string): string {
  if (kind === "deadline-exceeded") return "timeout"
  if (kind === "missing-session") return "runtime-session-missing"
  return kind
}

function buildTurnRequest(
  binding: { runtimeSessionId: string | null; workDir: string },
  prompt: string,
  options: { model?: string; variant?: string } | undefined,
  deadlineMs: number | undefined,
) {
  const modelOptions = options?.model ? parseModelIdentifier(options.model) : null
  return {
    target: {
      runtime: "opencode" as const,
      runtimeSessionId: binding.runtimeSessionId,
      workDir: binding.workDir,
    },
    prompt,
    deadlineMs: deadlineMs ?? DEFAULT_TURN_DEADLINE_MS,
    options: {
      model: modelOptions?.kind === "ok" ? { providerID: modelOptions.value.providerID, modelID: modelOptions.value.modelID } : null,
      variant: options?.variant ?? null,
      unknownKeys: undefined as readonly string[] | undefined,
    },
  }
}

function createReporter(
  context: ActionContext,
  sessionName: string,
  runtimeSessionId: string | null,
): WorkflowAgentSessionReporter | null {
  if (!context.projectId) return null
  const outbox = context.agentSessionRuntimeEventOutbox ?? null
  if (!outbox) return null
  if (!runtimeSessionId) return null
  return new WorkflowAgentSessionReporter({
    outbox,
    projectId: context.projectId,
    workflowRunId: context.workflowRunId,
    sessionName,
    workMetadata: {
      workId: context.workId,
      workType: context.workType,
      stage: context.stage ?? null,
    },
    randomId: context.runtimeEventRecordId ?? (() => `${Date.now()}_${Math.random().toString(36).slice(2)}`),
  })
}

function enqueueClose(
  reporter: WorkflowAgentSessionReporter | null,
  result: { ok: boolean; error?: { message: string } },
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
    failureReason: result.error?.message ?? "",
    runtimeSessionId,
  })
}

function opencodeAgent(context: ActionContext): AgentTurn | undefined {
  const runtime = context.openCodeRuntime
  if (!runtime) return undefined

  return {
    async turn(request: AgentTurnRequest): Promise<ActionResult> {
      const prompt = composeOpencodePrompt(request.prompt, context.parentIssueContext)

      if (!runtime.ready()) {
        const diagnostic = runtime.diagnostic()
        return actionFail("runtime-unavailable", `mohist/opencode requires the OpenCode runtime to be ready: ${diagnostic?.message ?? "no readiness diagnostic"}`)
      }

      const sessionName = request.session ?? context.workId
      let binding: { runtimeSessionId: string | null; workDir: string } | null = null

      if (context.serverConnection && context.projectId) {
        try {
          const opened = await context.serverConnection.openWorkflowAgentSession(
            context.projectId,
            context.workflowRunId,
            sessionName,
            {
              workId: context.workId,
              workType: context.workType,
              stage: context.stage,
              title: context.title,
              issueNumber: context.issueNumber,
              epicNumber: context.epicNumber,
              workDir: context.workDir,
            },
            context.signal,
          )
          if (opened.workDir && opened.workDir !== context.workDir) {
            return actionFail("session-workspace-mismatch", "Workflow AgentSession is bound to a different workspace; rerun the stage with a new task attempt before retrying")
          }
          binding = {
            runtimeSessionId: opened.runtimeSessionId ?? null,
            workDir: context.workDir,
          }
        } catch (error) {
          return actionFail("session-binding-failed", `Failed to resolve the Workflow AgentSession binding: ${actionErrorMessage(error)}`)
        }
      }

      if (!binding) {
        binding = { runtimeSessionId: null, workDir: context.workDir }
      }

      if (binding.runtimeSessionId === null && sessionName && context.serverConnection && context.projectId) {
        const created = await runtime.createSession({
          target: { runtime: "opencode", runtimeSessionId: null, workDir: binding.workDir },
          model: runtimeModel(request.options),
        })
        if (!created.ok) {
          return actionFail(runtimeErrorCode(created.error.kind), created.error.message, { exitCode: 1, turnFact: { finalAssistantText: null } })
        }
        try {
          await context.serverConnection.attachWorkflowAgentSession(
            context.projectId,
            context.workflowRunId,
            sessionName,
            {
              runtimeSessionId: created.value.runtimeSessionId,
              workDir: created.value.workDir,
              model: request.options?.model ?? null,
              workId: context.workId,
            },
            context.signal,
          )
        } catch (error) {
          return actionFail("session-binding-failed", `Failed to persist the Workflow AgentSession binding: ${actionErrorMessage(error)}`, { exitCode: 1, turnFact: { finalAssistantText: null } })
        }
        binding = {
          runtimeSessionId: created.value.runtimeSessionId,
          workDir: created.value.workDir,
        }
      }

      const turnRequest = buildTurnRequest(binding, prompt, request.options, request.deadlineMs)
      const reporter = createReporter(context, sessionName, binding.runtimeSessionId)
      if (reporter && binding.runtimeSessionId) {
        try {
          await reporter.awaitInput(prompt, binding.runtimeSessionId)
        } catch (error) {
          return actionFail("execution-unavailable", `failed to durably enqueue the Workflow AgentSession input: ${actionErrorMessage(error)}`)
        }
      }
      const observer = reporter ? { onEvent: (event: any) => reporter.registerEvent(event) } : undefined
      const result = await runtime.runTurn(turnRequest, context.signal, observer)
      enqueueClose(reporter, result, binding.runtimeSessionId)
      await reporter?.settle()
      if (!result.ok) {
        return actionFail(runtimeErrorCode(result.error.kind), result.error.message, { exitCode: 1, turnFact: { finalAssistantText: null } })
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

export async function callAction(
  action: (inputs: JsonObject, host: ActionHost) => Promise<ActionResult>,
  context: ActionContext,
): Promise<ActionResult> {
  const host = hostFromActionContext(context)
  host.agent = opencodeAgent(context)
  return action(context.with ?? {}, host)
}
