import type { CheckResult, StageContext, StageTaskResult } from '../stage-context';
import type { AgentSessionTaskInput } from './types';
import type { FailedCheckContext } from '../model';
import { createAgentSessionTaskHandler } from './agent-session-task-handler';

export type RepairFixTaskId =
  | 'fix-plan-health'
  | 'fix-build-health'
  | 'fix-check-health'
  | 'fix-integrate-health'
  | 'repair-plan-artifacts'
  | 'fix-review-findings'
  | 'repair-merge';

export interface RepairFixContext {
  worktreePath: string;
  failedCheck: CheckResult;
  attempt: number;
  failedCheckContext?: FailedCheckContext;
}

function isHealthFixTask(taskId: string): boolean {
  return taskId.startsWith('fix-') && taskId.endsWith('-health');
}

function isAgentSessionRepairTask(taskId: string): boolean {
  return taskId === 'repair-plan-artifacts' || taskId === 'fix-review-findings';
}

function isMergeRepairTask(taskId: string): boolean {
  return taskId === 'repair-merge';
}

export interface RepairFixAdapterOptions {
  agentSessionHandler?: ReturnType<typeof createAgentSessionTaskHandler>;
}

export function createRepairFixAdapter(options?: RepairFixAdapterOptions) {
  const agentHandler = options?.agentSessionHandler ?? createAgentSessionTaskHandler();

  return {
    async dispatch(
      taskId: RepairFixTaskId,
      ctx: StageContext,
      context: RepairFixContext,
    ): Promise<StageTaskResult> {
      if (isHealthFixTask(taskId)) {
        return dispatchHealthFix(taskId, ctx, context);
      }
      if (isAgentSessionRepairTask(taskId)) {
        return dispatchAgentSessionRepair(taskId, ctx, context);
      }
      if (isMergeRepairTask(taskId)) {
        return dispatchMergeRepair(ctx, context);
      }
      throw new Error(`Unknown repair/fix task id: ${taskId}`);
    },
  };

  async function dispatchHealthFix(
    taskId: string,
    ctx: StageContext,
    context: RepairFixContext,
  ): Promise<StageTaskResult> {
    const stage = taskId.replace('fix-', '').replace('-health', '') as 'plan' | 'build' | 'check' | 'integrate';
    const title = `Fix ${stage} health`;

    const input: AgentSessionTaskInput = {
      taskId,
      title,
      prompt: buildHealthFixPrompt(taskId, ctx, context),
      cwd: context.worktreePath,
      stage,
      attempt: context.attempt,
    };

    return agentHandler(input, ctx);
  }

  async function dispatchAgentSessionRepair(
    taskId: string,
    ctx: StageContext,
    context: RepairFixContext,
  ): Promise<StageTaskResult> {
    const stage = taskId === 'repair-plan-artifacts' ? 'plan' : 'check';
    const title = taskId === 'repair-plan-artifacts' ? 'Repair plan artifacts' : 'Fix review findings';

    const input: AgentSessionTaskInput = {
      taskId,
      title,
      prompt: buildAgentSessionRepairPrompt(taskId, ctx, context),
      cwd: context.worktreePath,
      stage,
      attempt: context.attempt,
    };

    return agentHandler(input, ctx);
  }

  async function dispatchMergeRepair(
    ctx: StageContext,
    context: RepairFixContext,
  ): Promise<StageTaskResult> {
    const taskId = 'repair-merge';
    const title = 'Repair merge readiness';
    const startedAt = Date.now();

    const project = ctx.projectRepo?.findById(ctx.issue.projectId);
    if (!project) {
      return {
        taskId,
        title,
        status: 'failed',
        artifacts: [],
        attempts: context.attempt,
        duration: Date.now() - startedAt,
        output: { kind: 'merge-repair', success: false, error: 'Project not found' },
      };
    }

    const worktreePath = ctx.worktreeManager.getPath(project.name, ctx.issue.number);
    if (!worktreePath) {
      return {
        taskId,
        title,
        status: 'failed',
        artifacts: [],
        attempts: context.attempt,
        duration: Date.now() - startedAt,
        output: { kind: 'merge-repair', success: false, error: 'Worktree not found' },
      };
    }

    try {
      const headBefore = await ctx.worktreeManager.getHeadSha(worktreePath);
      const output = context.failedCheck.output as { targetBranch?: string; conflictFiles?: string[] };
      const targetBranch = output?.targetBranch ?? project.baseBranch;

      const result = await ctx.worktreeManager.rebaseOntoMaster(
        project.path,
        project.name,
        ctx.issue.number,
        targetBranch,
        { abortOnConflict: false },
      );

      const headAfter = await ctx.worktreeManager.getHeadSha(worktreePath);
      const headChanged = headBefore !== headAfter;

      return {
        taskId,
        title,
        status: result.success ? 'completed' : 'failed',
        artifacts: [],
        attempts: context.attempt,
        duration: Date.now() - startedAt,
        reason: result.success
          ? `${title} completed after ${context.attempt} attempt(s)`
          : `merge conflict prevented successful merge repair`,
        causedBy: {
          type: 'conflict' as const,
          checkName: context.failedCheck.name,
          message: result.conflicts.length > 0 ? `Conflict files: ${result.conflicts.join(', ')}` : undefined,
        },
        output: {
          kind: 'merge-repair' as const,
          targetBranch,
          attempt: context.attempt,
          success: result.success,
          conflicts: result.conflicts,
          headChanged,
          headBefore,
          headAfter,
        },
      };
    } catch (err) {
      const error = err instanceof Error ? err.message : String(err);
      return {
        taskId,
        title,
        status: 'failed',
        artifacts: [],
        attempts: context.attempt,
        duration: Date.now() - startedAt,
        reason: `${title} failed: ${error}`,
        causedBy: {
          type: 'conflict' as const,
          checkName: context.failedCheck.name,
          message: error,
        },
        output: { kind: 'merge-repair' as const, success: false, error },
      };
    }
  }
}

function buildHealthFixPrompt(taskId: string, ctx: StageContext, context: RepairFixContext): string {
  const stage = taskId.replace('fix-', '').replace('-health', '') as 'plan' | 'build' | 'check' | 'integrate';
  const checkOutput = context.failedCheck.output != null ? JSON.stringify(context.failedCheck.output, null, 2) : '';
  const trimmedOutput = checkOutput.length > 12000 ? checkOutput.slice(-12000) : checkOutput;
  const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);

  return [
    `Change Directory: ${changeDir ?? context.worktreePath}`,
    '',
    `Issue: ${ctx.issue.title}`,
    `Stage: ${stage}`,
    `Failed check: ${context.failedCheck.name}`,
    '',
    'Failure Summary:',
    context.failedCheck.message ?? 'Health gate failed.',
    '',
    'Check Output:',
    trimmedOutput,
    '',
    'Contract: Apply the minimal code or artifact changes required to make the health command pass. Do not make unrelated refactors.',
  ].join('\n');
}

function buildAgentSessionRepairPrompt(taskId: string, ctx: StageContext, context: RepairFixContext): string {
  const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);

  if (taskId === 'repair-plan-artifacts') {
    const missing: string[] = [];
    const PLAN_ARTIFACT_FILES = ['proposal.md', 'specs', 'design.md', 'tasks.json', 'self-review.md'];
    for (const artifact of PLAN_ARTIFACT_FILES) {
      const fullPath = `${changeDir}/${artifact}`;
      if (!ctx.artifactManager.exists(fullPath)) {
        missing.push(artifact);
      }
    }

    return [
      `Change Directory: ${changeDir ?? context.worktreePath}`,
      '',
      `Issue: ${ctx.issue.title}`,
      `Failed check: ${context.failedCheck.name}`,
      '',
      'Failure Summary:',
      context.failedCheck.message ?? 'Plan artifact check failed.',
      '',
      missing.length > 0 ? `Missing or Invalid Artifacts:\n${missing.map(m => `- ${m}`).join('\n')}` : '',
      '',
      'Expected Durable Artifacts:',
      'proposal.md, specs, design.md, tasks.json, self-review.md',
      '',
      'Contract: Create or update only the missing or invalid artifact files under the change directory. Do not modify artifacts that already pass their checks.',
    ].filter(Boolean).join('\n');
  }

  if (taskId === 'fix-review-findings') {
    const structuredContext = context.failedCheckContext;
    const priorOutputsSection = formatPriorTaskOutputsSection(structuredContext?.priorTaskOutputs);

    if (structuredContext && structuredContext.blockingItems.length > 0) {
      const itemsSection = structuredContext.blockingItems.map(item => {
        const parts = [
          `- [ID: ${item.id}]`,
          `  Severity: ${item.severity}`,
          item.scope ? `  Scope: ${item.scope}` : '',
          `  Evidence: ${item.evidence}`,
          item.suggestedAction ? `  SuggestedAction: ${item.suggestedAction}` : '',
          item.verification ? `  Verification: ${item.verification}` : '',
        ];
        return parts.filter(Boolean).join('\n');
      }).join('\n\n');

      const nonBlockingSection = structuredContext.nonBlockingItems.length > 0
        ? '\n\nNon-blocking / Follow-up Items (do NOT fix these unless they directly overlap with a blocking item):\n' +
          structuredContext.nonBlockingItems.map(item =>
            `- [ID: ${item.id}] Severity: ${item.severity} Status: ${item.status ?? 'open'} — ${item.evidence}`
          ).join('\n')
        : '';

      const snapshotSection = structuredContext.snapshot?.sha
        ? `\n\nCandidate Snapshot SHA: ${structuredContext.snapshot.sha}`
        : '';

      return [
        `Change Directory: ${changeDir ?? context.worktreePath}`,
        '',
        `Issue: ${ctx.issue.title}`,
        `Failed check: ${context.failedCheck.name}`,
        '',
        `Blocking Items (${structuredContext.blockingItems.length}):`,
        'You MUST resolve ALL of these items:',
        '',
        itemsSection,
        nonBlockingSection,
        snapshotSection,
        priorOutputsSection,
        '',
        'Contract: Apply the minimal code or artifact changes required to resolve every listed blocking item. Do not modify review.md or review-self-check.md. Report which item IDs you attempted, resolved, and left unresolved.',
      ].join('\n');
    }

    const output = context.failedCheck.output as { verdict?: string; reviewReport?: string; fixSuggestions?: string } | undefined;
    const fixSuggestions = output?.fixSuggestions ?? '';
    const reviewReport = output?.reviewReport ?? '';
    const trimmedReport = reviewReport.length > 12000 ? reviewReport.slice(-12000) : reviewReport;
    const trimmedSuggestions = fixSuggestions.length > 8000 ? fixSuggestions.slice(-8000) : fixSuggestions;

    return [
      `Change Directory: ${changeDir ?? context.worktreePath}`,
      '',
      `Issue: ${ctx.issue.title}`,
      `Failed check: ${context.failedCheck.name}`,
      '',
      'Review Report:',
      trimmedReport,
      '',
      'Fix Suggestions:',
      trimmedSuggestions || 'No structured fix suggestions found. Read the review report carefully and address all FAIL items.',
      priorOutputsSection,
      '',
      'Contract: Apply the minimal code or artifact changes required to resolve every FAIL item. Do not modify review.md or review-self-check.md. Report which item IDs you attempted, resolved, and left unresolved.',
    ].join('\n');
  }

  return '';
}

function formatPriorTaskOutputsSection(priorTaskOutputs?: Record<string, unknown>[]): string {
  if (!priorTaskOutputs || priorTaskOutputs.length === 0) return '';
  const formatted = priorTaskOutputs.map((entry, index) => {
    const json = JSON.stringify(entry, null, 2);
    const trimmed = json.length > 4000 ? `${json.slice(0, 4000)}\n...` : json;
    return `Prior Context ${index + 1}:\n${trimmed}`;
  }).join('\n\n');
  return `\n\nSelected Prior Outputs:\n${formatted}`;
}

export const defaultRepairFixAdapter = createRepairFixAdapter();
