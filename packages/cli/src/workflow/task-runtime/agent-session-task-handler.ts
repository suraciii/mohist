import type { StageContext, StageTaskResult } from '../stage-context';
import type { AgentSessionTaskInput } from './types';
import { emitStageTaskUpdate } from '../stage-context';
import { AgentSession, createWorkflowSessionObservers, type AgentSessionOptions } from '../../agent-runtime';
import { extractReactionOutput } from '../convergence';

export interface AgentSessionTaskHandlerDeps {
  createSession?: (options: AgentSessionOptions) => Promise<AgentSession>;
  createObservers?: (ctx: StageContext, title: string, stage: string) => ReturnType<typeof createWorkflowSessionObservers>;
}

export function createAgentSessionTaskHandler(deps?: AgentSessionTaskHandlerDeps): (
  input: AgentSessionTaskInput,
  ctx: StageContext,
) => Promise<StageTaskResult> {
  return async function runAgentSessionTask(
    input: AgentSessionTaskInput,
    ctx: StageContext,
  ): Promise<StageTaskResult> {
    const startedAt = Date.now();
    const { taskId, title, prompt, cwd, stage, attempt } = input;

    emitStageTaskUpdate(
      ctx.eventBus,
      ctx.issue.id,
      ctx.issue.projectId,
      stage,
      taskId,
      title,
      'started',
      attempt,
      [],
    );

    const observers = deps?.createObservers
      ? deps.createObservers(ctx, title, stage)
      : createWorkflowSessionObservers({
          eventBus: ctx.eventBus,
          workflowLogRepo: ctx.workflowLogRepo,
          sessionStreamLogRepo: ctx.sessionStreamLogRepo,
          coderSessionRepo: ctx.coderSessionRepo,
          stage,
          title,
        });

    const acpOptions: AgentSessionOptions = {
      ...ctx.acpOptions,
      cwd,
      issueId: ctx.issue.id,
      projectId: ctx.issue.projectId,
      issueNumber: ctx.issue.number,
      executionId: `${stage}-${ctx.issue.number}-${taskId}-${attempt}`,
      stage,
      title,
      observers,
    };

    const createSessionFn = deps?.createSession ?? (async (opts: AgentSessionOptions) => {
      return AgentSession.create(opts);
    });

    const sharedRef = input.agentSessionRef;
    const isNamedSession = sharedRef != null && ctx.agentSessionRegistry != null;
    let session: AgentSession | undefined;
    let taskLocalSession = false;

    try {
      if (isNamedSession) {
        session = await ctx.agentSessionRegistry!.getOrCreate(sharedRef, () => createSessionFn(acpOptions));
      } else {
        session = await createSessionFn(acpOptions);
        taskLocalSession = true;
      }
      const result = await session!.execute(prompt, { kind: 'task', title });
      const duration = Date.now() - startedAt;
      const status = result.success ? 'completed' : 'failed';

      let artifacts: string[] = [];
      if (result.success && input.artifactVerification) {
        artifacts = input.artifactVerification([]);
      }

      const structuredResult = extractReactionOutput({
        taskId,
        title,
        status,
        artifacts,
        attempts: attempt,
        duration,
        output: {
          kind: 'agent-session-task',
          result: {
            structuredOutput: result.text,
          },
        },
      });

      emitStageTaskUpdate(
        ctx.eventBus,
        ctx.issue.id,
        ctx.issue.projectId,
        stage,
        taskId,
        title,
        status,
        attempt,
        artifacts,
      );

      return {
        taskId,
        title,
        status,
        artifacts,
        attempts: attempt,
        duration,
        output: {
          kind: 'agent-session-task',
          stage,
          attempt,
          success: result.success,
          error: result.error,
          acpSessionId: result.acpSessionId,
          agentSessionRef: input.agentSessionRef,
          result: {
            ...(structuredResult ?? {}),
            structuredOutput: result.text,
          },
          summary: result.success
            ? `${title} completed`
            : `${title} failed: ${result.error ?? 'unknown error'}`,
        },
      };
    } catch (err) {
      const duration = Date.now() - startedAt;
      const error = err instanceof Error ? err.message : String(err);

      emitStageTaskUpdate(
        ctx.eventBus,
        ctx.issue.id,
        ctx.issue.projectId,
        stage,
        taskId,
        title,
        'failed',
        attempt,
        [],
      );

      return {
        taskId,
        title,
        status: 'failed',
        artifacts: [],
        attempts: attempt,
        duration,
        output: {
          kind: 'agent-session-task',
          stage,
          attempt,
          success: false,
          error,
        },
      };
    } finally {
      if (taskLocalSession && session !== undefined) {
        await session.close().catch(() => {});
      }
    }
  };
}

export const defaultAgentSessionTaskHandler = createAgentSessionTaskHandler();
