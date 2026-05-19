import * as fs from 'fs';
import * as path from 'path';
import { Stage, MergeState } from '../../types';
import type { StageContext, StageTaskResult, CheckResult } from '../stage-context';
import { emitStageTaskUpdate } from '../stage-context';
import type { ExecutableTask, TaskKind } from './types';
import type { AgentPromptSource, TaskDefinition, TaskExecutionKind } from '../domain';
import { executeRebaseBranchTask } from './rebase-task-handler';
import { createRepairFixAdapter, type RepairFixTaskId } from './repair-fix-adapter';
import { buildArtifactPrompt, buildSelfReviewPrompt, buildReviewerPrompt } from '../../agents/artifact-prompt';
import { OpenSpecIntegrator } from '../../openspec/open-spec-integrator';
import { loadVerificationContext, buildVerificationPromptSuffix } from '../convergence';

interface PlanTaskConfig {
  type: string;
  promptRef: string;
  label: string;
  verifyArtifact: () => boolean;
}

export type DispatchableTask = ExecutableTask | {
  taskId: string;
  title: string;
  kind: 'agent-session' | 'service-call' | 'ralph-task';
  prompt?: string;
  cwd?: string;
  stage?: string;
  attempt?: number;
  agentSessionRef?: string;
  artifactVerification?: (artifacts: string[]) => string[];
  serviceFn?: (ctx: StageContext) => Promise<unknown>;
  input?: unknown;
};

export interface TaskDispatchFactoryInput {
  ctx: StageContext;
  task: ExecutableTask;
  executionKind: TaskExecutionKind | TaskKind;
  attempt: number;
  failedCheck?: CheckResult;
  worktreePath: string;
  agentSessionRef?: string;
  sourceTask?: TaskDefinition;
}

export interface TaskDispatchFactoryRegistry {
  build(input: TaskDispatchFactoryInput): DispatchableTask | null;
}

export function createDefaultTaskDispatchFactoryRegistry(): TaskDispatchFactoryRegistry {
  const integrator = new OpenSpecIntegrator();
  return {
    build(input) {
      if (input.executionKind === 'rebase-task') return createRebaseDispatchTask(input);
      if (input.executionKind === 'repair-task') return createRepairDispatchTask(input);
      if (input.executionKind === 'service-call') return createServiceCallDispatchTask(input, integrator);
      if (input.executionKind === 'agent-session') return createAgentSessionDispatchTask(input);
      return { ...input.task, kind: 'ralph-task' };
    },
  };
}

function createPlanTaskConfigs(changeDir: string): PlanTaskConfig[] {
  return [
    {
      type: 'proposal',
      promptRef: 'mohist/plan/proposal',
      label: 'proposal.md',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'proposal.md')),
    },
    {
      type: 'specs',
      promptRef: 'mohist/plan/specs',
      label: 'specs/',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'specs')),
    },
    {
      type: 'design',
      promptRef: 'mohist/plan/design',
      label: 'design.md',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'design.md')),
    },
    {
      type: 'tasks',
      promptRef: 'mohist/plan/tasks',
      label: 'tasks.json',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'tasks.json')),
    },
    {
      type: 'self-review',
      promptRef: 'mohist/plan/self-review',
      label: 'self-review.md',
      verifyArtifact: () => fs.existsSync(path.join(changeDir, 'self-review.md')),
    },
  ];
}

function createRebaseDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn: async () => {
      const result = await executeRebaseBranchTask(input.ctx, input.attempt);
      if (result.status !== 'completed') {
        throw new Error(result.reason ?? 'Rebase branch failed');
      }
      return result.output;
    },
  };
}

function createRepairDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn: async () => {
      const adapter = createRepairFixAdapter();
      const result = await adapter.dispatch(normalizeRepairTaskId(input.task.taskId), input.ctx, {
        worktreePath: input.worktreePath,
        failedCheck: defaultFailedCheckForTask(input.task.taskId, input.failedCheck),
        attempt: input.attempt,
      });
      if (result.status !== 'completed') {
        throw new Error(result.reason ?? `${input.task.title} failed`);
      }
      return result.output;
    },
  };
}

function createServiceCallDispatchTask(input: TaskDispatchFactoryInput, integrator: OpenSpecIntegrator): DispatchableTask | null {
  if (input.task.taskId === 'check:converge-review-snapshot') {
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => {
        const result = await executeConvergeReviewSnapshotTask(input.ctx);
        if (result.status !== 'completed') {
          throw new Error(result.reason ?? 'Converge review snapshot failed');
        }
        return result.output;
      },
    };
  }

  if (input.ctx.issue.stage !== Stage.Integrate) return null;
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn: buildIntegrateServiceFn(input.task.taskId, input.worktreePath, integrator),
  };
}

function createAgentSessionDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask | null {
  const promptSource = agentPromptSource(input.sourceTask);
  if (promptSource && !isBuiltinAgentPromptRef(promptSource)) {
    return createGenericAgentSessionDispatchTask(input, resolveCustomAgentPrompt(input, promptSource));
  }
  if (typeof input.task.prompt === 'string' && input.task.prompt.trim().length > 0) {
    return createGenericAgentSessionDispatchTask(input, input.task.prompt);
  }
  if (input.ctx.issue.stage === Stage.Plan) return createPlanAgentSessionDispatchTask(input);
  if (input.ctx.issue.stage === Stage.Check && input.task.taskId === 'ai-review') return createCheckAiReviewDispatchTask(input);
  return { ...input.task, agentSessionRef: input.agentSessionRef };
}

function agentPromptSource(task: TaskDefinition | undefined): AgentPromptSource | null {
  const rawPrompt = task?.with?.prompt;
  if (typeof rawPrompt === 'string') return { inline: rawPrompt };
  if (!rawPrompt || typeof rawPrompt !== 'object' || Array.isArray(rawPrompt)) return null;
  const prompt = rawPrompt as Record<string, unknown>;
  if (typeof prompt.ref === 'string') return { ref: prompt.ref };
  if (typeof prompt.file === 'string') return { file: prompt.file };
  if (typeof prompt.inline === 'string') return { inline: prompt.inline };
  return null;
}

function isBuiltinAgentPromptRef(source: AgentPromptSource): boolean {
  return 'ref' in source && source.ref.startsWith('mohist/');
}

function builtinAgentPromptRef(input: TaskDispatchFactoryInput, fallback: string): string {
  const source = agentPromptSource(input.sourceTask);
  if (source && 'ref' in source && source.ref.startsWith('mohist/')) return source.ref;
  return fallback;
}

function resolveCustomAgentPrompt(input: TaskDispatchFactoryInput, source: AgentPromptSource): string {
  if ('inline' in source) return source.inline;
  if ('file' in source) {
    const promptPath = path.isAbsolute(source.file) ? source.file : path.join(input.worktreePath, source.file);
    return fs.readFileSync(promptPath, 'utf-8');
  }
  throw new Error(`Unknown agent prompt ref '${source.ref}' for task '${input.task.taskId}'`);
}

function createGenericAgentSessionDispatchTask(input: TaskDispatchFactoryInput, prompt: string): DispatchableTask {
  const declaredArtifacts = extractStringArray((input.task.input as { artifacts?: unknown; outputs?: unknown } | undefined)?.artifacts)
    ?? extractStringArray((input.task.input as { outputs?: unknown } | undefined)?.outputs)
    ?? [];
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'agent-session',
    prompt,
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    agentSessionRef: input.agentSessionRef,
    artifactVerification: () => declaredArtifacts.filter(artifact => fs.existsSync(path.join(input.worktreePath, artifact))),
    input: input.task.input,
  };
}

function extractStringArray(value: unknown): string[] | null {
  if (!Array.isArray(value)) return null;
  return value.filter((item): item is string => typeof item === 'string');
}

function createPlanAgentSessionDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  const changeDir = input.ctx.artifactManager.getChangeDir(input.ctx.issue.number)
    || input.ctx.artifactManager.createChangeDir(input.ctx.issue.number, input.ctx.issue.title);
  if (!changeDir) throw new Error(`Failed to get or create change directory for issue #${input.ctx.issue.number}`);

  const tasks = createPlanTaskConfigs(changeDir);
  const taskConfig = tasks.find(candidate => candidate.type === input.task.taskId);
  if (!taskConfig) throw new Error(`Unknown Plan task: ${input.task.taskId}`);

  const completedSteps = input.ctx.checkpointManager.getResumeSteps(input.ctx.issue.number, 'plan');
  if (completedSteps.includes(taskConfig.type) && taskConfig.verifyArtifact()) {
    emitStageTaskUpdate(input.ctx.eventBus, input.ctx.issue.id, input.ctx.issue.projectId, input.ctx.issue.stage, input.task.taskId, input.task.title, 'completed', input.attempt, []);
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => ({ restoredFromCheckpoint: true }),
    };
  }
  if (taskConfig.verifyArtifact()) {
    input.ctx.checkpointManager.markStepComplete(input.ctx.issue.number, 'plan', taskConfig.type, tasks[tasks.indexOf(taskConfig) + 1]?.type ?? null);
    emitStageTaskUpdate(input.ctx.eventBus, input.ctx.issue.id, input.ctx.issue.projectId, input.ctx.issue.stage, input.task.taskId, input.task.title, 'completed', input.attempt, [taskConfig.label]);
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => ({ artifacts: [taskConfig.label], restoredFromDisk: true }),
    };
  }

  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'agent-session',
    prompt: buildBuiltinAgentPrompt(input, builtinAgentPromptRef(input, taskConfig.promptRef), changeDir),
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: 'plan',
    attempt: input.attempt,
    agentSessionRef: input.agentSessionRef,
    artifactVerification: () => taskConfig.verifyArtifact() ? [taskConfig.label] : [],
  };
}

function createCheckAiReviewDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  const changeDir = input.ctx.artifactManager.getChangeDir(input.ctx.issue.number)
    || input.ctx.artifactManager.createChangeDir(input.ctx.issue.number, input.ctx.issue.title);
  if (!changeDir) throw new Error(`Failed to get or create change directory for issue #${input.ctx.issue.number}`);

  const reviewOutputPath = 'review.md';
  const completedSteps = input.ctx.checkpointManager.getResumeSteps(input.ctx.issue.number, 'check');
  if (completedSteps.includes(input.task.taskId) && fs.existsSync(path.join(changeDir, reviewOutputPath))) {
    emitStageTaskUpdate(input.ctx.eventBus, input.ctx.issue.id, input.ctx.issue.projectId, input.ctx.issue.stage, input.task.taskId, input.task.title, 'completed', input.attempt, []);
    return {
      taskId: input.task.taskId,
      title: input.task.title,
      kind: 'service-call',
      stage: input.ctx.issue.stage,
      attempt: input.attempt,
      serviceFn: async () => ({ restoredFromCheckpoint: true }),
    };
  }

  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'agent-session',
    prompt: buildBuiltinAgentPrompt(input, builtinAgentPromptRef(input, 'mohist/check/ai-review'), changeDir),
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: 'check',
    attempt: input.attempt,
    agentSessionRef: input.agentSessionRef,
    artifactVerification: () => fs.existsSync(path.join(changeDir, reviewOutputPath)) ? [reviewOutputPath] : [],
    input: { mode: 'ai-review' },
  };
}

function buildBuiltinAgentPrompt(input: TaskDispatchFactoryInput, promptRef: string, changeDir: string): string {
  switch (promptRef) {
    case 'mohist/plan/proposal':
      return buildArtifactPrompt('proposal', input.ctx.issue, changeDir);
    case 'mohist/plan/specs':
      return buildArtifactPrompt('specs', input.ctx.issue, changeDir);
    case 'mohist/plan/design':
      return buildArtifactPrompt('design', input.ctx.issue, changeDir);
    case 'mohist/plan/tasks':
      return buildArtifactPrompt('tasks', input.ctx.issue, changeDir);
    case 'mohist/plan/self-review':
      return buildSelfReviewPrompt(input.ctx.issue, changeDir);
    case 'mohist/check/ai-review':
      return buildCheckReviewPrompt(input, changeDir);
    default:
      throw new Error(`Unknown built-in agent prompt ref '${promptRef}' for task '${input.task.taskId}'`);
  }
}

function buildCheckReviewPrompt(input: TaskDispatchFactoryInput, changeDir: string): string {
  const basePrompt = buildReviewerPrompt(input.ctx.issue, changeDir);
  const verificationCtx = loadVerificationContext(changeDir);
  if (!verificationCtx) return basePrompt;
  return basePrompt + buildVerificationPromptSuffix(verificationCtx);
}

function buildIntegrateServiceFn(taskId: string, worktreePath: string, integrator: OpenSpecIntegrator): (ctx: StageContext) => Promise<unknown> {
  if (taskId === 'integrate:spec-sync') {
    return async (ctx) => {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      if (!changeDir) throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
      const summary = await integrator.apply(changeDir, worktreePath);
      return {
        step: 'integrate:spec-sync' as const,
        capabilities: summary.capabilities,
        counts: { added: summary.added, modified: summary.modified, removed: summary.removed, renamed: summary.renamed },
        targetFiles: summary.targetFiles,
        conflicts: summary.conflicts,
        corrections: summary.corrections,
        valid: summary.valid,
        errors: summary.errors,
        mode: summary.mode,
      };
    };
  }

  if (taskId === 'integrate:archive-change') {
    return async (ctx) => {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      if (!changeDir) throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
      await ctx.artifactManager.archiveChange(ctx.issue.number);
      return { step: 'integrate:archive-change' as const, archivePath: path.relative(worktreePath, changeDir), success: true };
    };
  }

  if (taskId === 'integrate:merge') {
    return async (ctx) => {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      if (!project) throw new Error(`Project not found: ${ctx.issue.projectId}`);
      const baseBranch = project.baseBranch;

      if (ctx.issue.mergeState === MergeState.Merged) {
        const delivery = recoverMergeDelivery(ctx, baseBranch);
        if (!delivery) throw new Error('Issue is already marked merged but merge delivery evidence is missing');
        return { step: 'integrate:merge' as const, ...delivery, skipped: true, reason: 'already-merged' };
      }
      if (!ctx.worktreeManager.mergeApprovedCandidate) throw new Error('worktreeManager.mergeApprovedCandidate is not available');

      const mergeTruth = await ctx.worktreeManager.mergeApprovedCandidate(project.path, project.name, ctx.issue.number, baseBranch);
      if ('failingStep' in mergeTruth) {
        throw new Error(`Merge failed at ${mergeTruth.failingStep}: ${mergeTruth.error}` + (mergeTruth.conflictFiles?.length ? ` Conflicting files: ${mergeTruth.conflictFiles.join(', ')}` : ''));
      }
      if (ctx.issueRepo.setMergeState) ctx.issueRepo.setMergeState(ctx.issue.id, MergeState.Merged);
      return {
        step: 'integrate:merge' as const,
        targetBranch: mergeTruth.targetBranch,
        baseSha: mergeTruth.baseSha,
        candidateHeadSha: mergeTruth.candidateHeadSha,
        landedSha: mergeTruth.landedSha,
        rebased: mergeTruth.rebased,
      };
    };
  }

  throw new Error(`Unknown integrate task: ${taskId}`);
}

function recoverMergeDelivery(ctx: StageContext, targetBranch: string): { targetBranch: string; baseSha?: string; candidateHeadSha?: string; landedSha: string; rebased?: boolean } | null {
  const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
  const mergeTask = stageRun?.tasks.find(task => task.taskId === 'integrate:merge' && task.status === 'completed');
  const mergeOutput = unwrapWorkflowOutput(mergeTask?.output);
  if (typeof mergeOutput?.landedSha === 'string' && mergeOutput.landedSha.length > 0) {
    return {
      targetBranch: typeof mergeOutput.targetBranch === 'string' ? mergeOutput.targetBranch : targetBranch,
      baseSha: typeof mergeOutput.baseSha === 'string' ? mergeOutput.baseSha : undefined,
      candidateHeadSha: typeof mergeOutput.candidateHeadSha === 'string' ? mergeOutput.candidateHeadSha : undefined,
      landedSha: mergeOutput.landedSha,
      rebased: typeof mergeOutput.rebased === 'boolean' ? mergeOutput.rebased : undefined,
    };
  }

  return null;
}

function unwrapWorkflowOutput(output: unknown): Record<string, unknown> | null {
  if (!output || typeof output !== 'object') return null;
  const data = output as Record<string, unknown>;
  if (data.kind === 'service-call-task' && data.result && typeof data.result === 'object') {
    return data.result as Record<string, unknown>;
  }
  return data;
}

async function executeConvergeReviewSnapshotTask(ctx: StageContext): Promise<StageTaskResult> {
  const taskId = 'check:converge-review-snapshot';
  const title = 'Converge review snapshot';
  const startedAt = Date.now();
  emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId, ctx.issue.stage, taskId, title, 'started', 1, []);

  try {
    const project = ctx.projectRepo?.findById(ctx.issue.projectId);
    const worktreePath = project ? ctx.worktreeManager.getPath(project.name, ctx.issue.number) : null;
    if (!worktreePath) throw new Error('Worktree not found');
    const convergence = await ctx.worktreeManager.createCheckConvergenceCommit(worktreePath, ctx.issue.number);
    if (!convergence.success) throw new Error(convergence.error ?? 'Convergence commit failed');

    const output = { converged: true, snapshotSha: convergence.headSha };
    const latestReviewPassed = getLatestCheckResultFromStage(ctx, 'review-passed');
    if (latestReviewPassed && ctx.checkSuiteRepo) {
      const { buildAuthoritativeAiReviewResult } = await import('../stage-context');
      const authoritative = buildAuthoritativeAiReviewResult(
        { ...latestReviewPassed, output: { ...((latestReviewPassed.output as Record<string, unknown>) ?? {}), snapshotSha: convergence.headSha } },
        { snapshotSha: convergence.headSha },
      );
      const suite = authoritative ? ctx.checkSuiteRepo.findActiveByIssueId(ctx.issue.id) : null;
      if (suite && authoritative) {
        ctx.checkSuiteRepo.updateChecks(suite.id, 'review-passed', { status: 'passed', output: authoritative, ranAt: authoritative.convergedAt });
        ctx.checkSuiteRepo.updateSnapshotSha(suite.id, convergence.headSha);
      }
    }

    emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId, ctx.issue.stage, taskId, title, 'completed', 1, []);
    return { taskId, title, status: 'completed', artifacts: [], attempts: 1, duration: Date.now() - startedAt, output };
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ctx.issue.projectId, ctx.issue.stage, taskId, title, 'failed', 1, []);
    return { taskId, title, status: 'failed', artifacts: [], attempts: 1, duration: Date.now() - startedAt, reason: message, output: { error: message } };
  }
}

function getLatestCheckResultFromStage(ctx: StageContext, checkName: string): { name: string; status: 'pass' | 'fail' | 'error' | 'pending'; output?: unknown } | undefined {
  const checkStage = ctx.workflowRun?.stageRuns.find(stageRun => stageRun.stage === ctx.issue.stage);
  const check = checkStage?.checks.find(candidate => candidate.checkName === checkName);
  if (!check) return undefined;
  return {
    name: check.checkName,
    status: check.status === 'passed' ? 'pass' : check.status === 'pending' || check.status === 'running' ? 'pending' : check.status === 'error' ? 'error' : 'fail',
    output: check.output ?? undefined,
  };
}

function normalizeRepairTaskId(taskId: string): RepairFixTaskId {
  const baseTaskId = taskId.replace(/:\d+$/, '');
  if (baseTaskId === 'fix-merge-readiness') return 'repair-merge';
  if (baseTaskId === 'fix-plan-review') return 'repair-plan-artifacts';
  return baseTaskId as RepairFixTaskId;
}

function defaultFailedCheckForTask(taskId: string, failedCheck?: CheckResult): CheckResult {
  if (failedCheck) return failedCheck;
  const baseTaskId = taskId.replace(/:\d+$/, '');
  const defaults: Record<string, CheckResult> = {
    'fix-build-health': { name: 'health:build', status: 'fail' },
    'fix-check-health': { name: 'health:check', status: 'fail' },
    'fix-integrate-health': { name: 'health:integrate', status: 'fail' },
    'fix-review-findings': { name: 'review-passed', status: 'fail', output: { verdict: 'FAIL' } },
    'fix-merge-readiness': { name: 'merge-ready', status: 'fail' },
    'fix-plan-review': { name: 'self-review-passed', status: 'fail' },
  };
  return defaults[baseTaskId] ?? { name: taskId, status: 'fail' };
}
