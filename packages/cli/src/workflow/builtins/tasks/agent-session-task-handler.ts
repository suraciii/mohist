import * as fs from 'fs';
import type { StageContext, StageTaskResult } from '../../stage-context';
import type { AgentSessionTaskInput } from '../../tasks/types';
import { emitStageTaskUpdate } from '../../stage-context';
import { AgentSession, createWorkflowSessionObservers, type AgentSessionOptions } from '../../../agent-runtime';
import { extractReactionOutput } from '../../reaction/convergence';
import type { RequiredMarkerDefinition } from './agent-required-markers';
import { isParseSuccess, validateMarkerFile } from '../../result-contracts';

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
    const sharedRef = input.agentSessionRef;
    const isNamedSession = sharedRef != null && ctx.agentSessionRegistry != null;
    let session: AgentSession | undefined;
    let taskLocalSession = false;

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

    try {
      const worktreeBefore = await captureWorktreeChangeState(ctx, cwd);
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

      if (isNamedSession) {
        session = await ctx.agentSessionRegistry!.getOrCreate(sharedRef, () => createSessionFn(acpOptions));
      } else {
        session = await createSessionFn(acpOptions);
        taskLocalSession = true;
      }
      const result = await session!.execute(prompt, { kind: 'task', title });
      const markerResult = result.success
        ? await satisfyRequiredMarkers(session!, input.requiredMarkers, title)
        : { success: true, missing: [] as RequiredMarkerDefinition[], attempts: 0 };
      const duration = Date.now() - startedAt;
      const status = result.success && markerResult.success ? 'completed' : 'failed';

      let artifacts: string[] = [];
      if (status === 'completed' && input.artifactVerification) {
        artifacts = input.artifactVerification([]);
      }
      const events = status === 'completed'
        ? await raisedEventsForAgentTask(ctx, cwd, result.text, worktreeBefore)
        : [];

      const structuredResult = extractReactionOutput({
        taskId,
        title,
        status,
        artifacts,
        events,
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
        events,
        attempts: attempt,
        duration,
        output: {
          kind: 'agent-session-task',
          stage,
          attempt,
          success: result.success,
          error: result.error ?? (markerResult.success ? undefined : `Missing required marker in ${markerResult.missing.map(marker => marker.path).join(', ')}`),
          acpSessionId: result.acpSessionId,
          agentSessionRef: input.agentSessionRef,
          result: {
            ...(structuredResult ?? {}),
            structuredOutput: result.text,
          },
          summary: status === 'completed'
            ? `${title} completed`
            : `${title} failed: ${result.error ?? `missing required marker in ${markerResult.missing.map(marker => marker.path).join(', ')}`}`,
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
        events: [],
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

interface WorktreeChangeState {
  headSha: string | null;
  signature: string | null;
}

async function captureWorktreeChangeState(ctx: StageContext, cwd: string): Promise<WorktreeChangeState> {
  if (typeof ctx.worktreeManager.getHeadSha !== 'function') {
    return { headSha: null, signature: null };
  }
  const [headSha, signature] = await Promise.all([
    ctx.worktreeManager.getHeadSha(cwd).catch(() => null),
    captureWorktreeSignature(ctx, cwd),
  ]);
  return { headSha, signature };
}

async function didWorktreeChange(ctx: StageContext, cwd: string, before: WorktreeChangeState | null): Promise<boolean> {
  if (!before) return false;
  const after = await captureWorktreeChangeState(ctx, cwd);
  return before.headSha !== after.headSha || before.signature !== after.signature;
}

async function captureWorktreeSignature(ctx: StageContext, cwd: string): Promise<string | null> {
  if (ctx.worktreeManager.getWorktreeChangeSignature) {
    return ctx.worktreeManager.getWorktreeChangeSignature(cwd).catch(() => null);
  }
  if (typeof ctx.worktreeManager.isWorktreeClean !== 'function') {
    return null;
  }
  return ctx.worktreeManager.isWorktreeClean(cwd)
    .then(clean => clean ? '' : 'dirty')
    .catch(() => null);
}

async function raisedEventsForAgentTask(
  ctx: StageContext,
  cwd: string,
  structuredOutput: unknown,
  worktreeBefore: WorktreeChangeState | null,
): Promise<string[]> {
  const events: string[] = [];
  if (await didWorktreeChange(ctx, cwd, worktreeBefore)) {
    events.push('code.changed');
  }
  events.push(...extractEventsFromAgentOutput(structuredOutput));
  return events;
}

function extractEventsFromAgentOutput(output: unknown): string[] {
  if (typeof output !== 'string') return [];
  const events = new Set<string>();
  for (const eventName of extractWorkflowEventMarkers(output)) {
    events.add(eventName);
  }

  const structured = parseJsonObject(output);
  const values = structured ? stringArrayValue(structured.events) : [];
  for (const eventName of values) {
    events.add(eventName);
  }
  return [...events];
}

function extractWorkflowEventMarkers(output: string): string[] {
  const events: string[] = [];
  const regex = /<workflow-event>\s*([^<]+?)\s*<\/workflow-event>/gi;
  let match: RegExpExecArray | null;
  while ((match = regex.exec(output)) !== null) {
    const eventName = match[1]?.trim();
    if (eventName) events.push(eventName);
  }
  return events;
}

function parseJsonObject(output: string): Record<string, unknown> | null {
  try {
    const parsed = JSON.parse(output);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : null;
  } catch {
    return null;
  }
}

function stringArrayValue(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.filter((item): item is string => typeof item === 'string');
}

async function satisfyRequiredMarkers(
  session: AgentSession,
  markers: RequiredMarkerDefinition[] | undefined,
  title: string,
): Promise<{ success: true; missing: RequiredMarkerDefinition[]; attempts: number } | { success: false; missing: RequiredMarkerDefinition[]; attempts: number }> {
  if (!markers || markers.length === 0) return { success: true, missing: [], attempts: 0 };

  let missing = missingRequiredMarkers(markers);
  if (missing.length === 0) return { success: true, missing, attempts: 0 };

  const maxAttempts = Math.max(...missing.map(marker => marker.onMissing?.maxAttempts ?? 0));
  if (maxAttempts <= 0 || missing.some(marker => marker.onMissing?.action !== 'continue-session')) {
    return { success: false, missing, attempts: 0 };
  }

  for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
    await session.execute(buildMissingMarkerPrompt(missing), { kind: 'task', title: `${title} marker completion` });
    missing = missingRequiredMarkers(markers);
    if (missing.length === 0) return { success: true, missing, attempts: attempt };
  }

  return { success: false, missing, attempts: maxAttempts };
}

function missingRequiredMarkers(markers: RequiredMarkerDefinition[]): RequiredMarkerDefinition[] {
  return markers.filter(marker => {
    const content = readMarkerFile(marker.path);
    const parsed = validateMarkerFile(marker.path, content, marker.markers);
    if (!isParseSuccess(parsed)) return true;
    return !marker.markers.some(candidate => candidate.toUpperCase() === parsed.marker.toUpperCase());
  });
}

function readMarkerFile(filePath: string): string | null {
  try {
    if (!fs.existsSync(filePath)) return null;
    const content = fs.readFileSync(filePath, 'utf-8');
    return content.length > 0 ? content : null;
  } catch {
    return null;
  }
}

function buildMissingMarkerPrompt(markers: RequiredMarkerDefinition[]): string {
  const lines = [
    'The previous response did not satisfy the required workflow marker contract.',
    '',
    'Update the following file(s) so each contains exactly one valid required marker from its allowed set.',
    'Do not change unrelated files.',
    '',
  ];
  for (const marker of markers) {
    lines.push(`- ${marker.path}`);
    lines.push(`  Allowed markers: ${marker.markers.join(', ')}`);
  }
  return lines.join('\n');
}
