import * as fs from 'fs';
import * as path from 'path';
import { Stage, MergeState } from '../../types';
import type { StageContext, CheckResult } from '../stage-context';
import { emitStageTaskUpdate } from '../stage-context';
import type { ExecutableTask, TaskKind } from './types';
import type { AgentPromptSource, TaskDefinition, TaskExecutionKind } from '../domain';
import type { RequiredMarkerDefinition } from './agent-required-markers';
import { createWorkflowTemplateContext, renderWorkflowTemplate, workflowDefinitionSnapshotFromUnknown } from '../domain';
import { executeRebaseBranchTask } from './rebase-task-handler';
import { createRepairFixAdapter, type RepairFixTaskId } from './repair-fix-adapter';
import { buildArtifactPrompt, buildSelfReviewPrompt, buildReviewerPrompt } from '../../agents/artifact-prompt';
import { OpenSpecIntegrator } from '../../openspec/open-spec-integrator';
import { loadVerificationContext, buildVerificationPromptSuffix } from '../convergence';
import { inferWorkflowTaskUse } from '../uses-catalog';

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
  requiredMarkers?: RequiredMarkerDefinition[];
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

function baseRuntimeTaskId(taskId: string): string {
  return taskId.replace(/:\d+$/, '');
}

function createRebaseDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn: async () => {
      const result = await executeRebaseBranchTask(input.ctx, input.attempt, {
        taskId: input.task.taskId,
        title: input.task.title,
      });
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
  const uses = input.sourceTask?.uses ?? inferWorkflowTaskUse(input.task.taskId, input.executionKind);
  const serviceFn = buildWorkflowServiceFn(uses, input, integrator);
  if (!serviceFn) return null;

  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'service-call',
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn,
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
  if (input.ctx.issue.stage === Stage.Check && baseRuntimeTaskId(input.task.taskId) === 'ai-review') return createCheckAiReviewDispatchTask(input);
  return {
    ...input.task,
    agentSessionRef: input.agentSessionRef,
  };
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
  if ('inline' in source) return renderPromptTemplate(input, source.inline);
  if ('file' in source) {
    const promptPath = path.isAbsolute(source.file) ? source.file : path.join(input.worktreePath, source.file);
    return renderPromptTemplate(input, fs.readFileSync(promptPath, 'utf-8'));
  }
  throw new Error(`Unknown agent prompt ref '${source.ref}' for task '${input.task.taskId}'`);
}

function renderPromptTemplate(input: TaskDispatchFactoryInput, template: string): string {
  return renderWorkflowTemplate(template, buildTaskTemplateContext(input));
}

function buildTaskTemplateContext(input: TaskDispatchFactoryInput) {
  return createWorkflowTemplateContext({
    ctx: input.ctx,
    worktreePath: input.worktreePath,
    snapshot: workflowDefinitionSnapshotFromUnknown(input.ctx.workflowRun?.workflowDefinition),
  });
}

function requiredMarkersForTask(input: TaskDispatchFactoryInput): RequiredMarkerDefinition[] | undefined {
  const markers = input.sourceTask?.with?.requiredMarkers;
  if (!Array.isArray(markers)) return undefined;
  const rendered = markers
    .filter(isRequiredMarkerRecord)
    .map(marker => ({
      path: renderPromptTemplate(input, marker.path),
      markers: marker.markers,
      onMissing: marker.onMissing,
    }));
  return rendered.length > 0 ? rendered : undefined;
}

function createGenericAgentSessionDispatchTask(input: TaskDispatchFactoryInput, prompt: string): DispatchableTask {
  const declaredArtifacts = extractStringArray((input.task.input as { artifacts?: unknown; outputs?: unknown } | undefined)?.artifacts)
    ?? extractStringArray((input.task.input as { outputs?: unknown } | undefined)?.outputs)
    ?? [];
  const agentInput = {
    taskId: input.task.taskId,
    title: input.task.title,
    prompt,
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    agentSessionRef: input.agentSessionRef,
    requiredMarkers: requiredMarkersForTask(input),
    artifactVerification: () => declaredArtifacts.filter(artifact => fs.existsSync(path.join(input.worktreePath, artifact))),
  };
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    kind: 'agent-session',
    prompt,
    cwd: agentInput.cwd,
    stage: agentInput.stage,
    attempt: agentInput.attempt,
    agentSessionRef: input.agentSessionRef,
    requiredMarkers: agentInput.requiredMarkers,
    artifactVerification: agentInput.artifactVerification,
    input: agentInput,
  };
}

function isRequiredMarkerRecord(value: unknown): value is RequiredMarkerDefinition {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const data = value as Record<string, unknown>;
  if (typeof data.path !== 'string') return false;
  if (!Array.isArray(data.markers) || !data.markers.every(marker => typeof marker === 'string')) return false;
  if (data.onMissing === undefined) return true;
  if (!data.onMissing || typeof data.onMissing !== 'object' || Array.isArray(data.onMissing)) return false;
  const onMissing = data.onMissing as Record<string, unknown>;
  return onMissing.action === 'continue-session'
    && (onMissing.maxAttempts === undefined || typeof onMissing.maxAttempts === 'number');
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
  const baseTaskId = baseRuntimeTaskId(input.task.taskId);
  const taskConfig = tasks.find(candidate => candidate.type === input.task.taskId || candidate.type === baseTaskId);
  if (!taskConfig) throw new Error(`Unknown Plan task: ${input.task.taskId}`);

  const completedSteps = input.ctx.checkpointManager.getResumeSteps(input.ctx.issue.number, 'plan');
  if (mayRestoreTaskFromPriorOutput(input) && completedSteps.includes(taskConfig.type) && taskConfig.verifyArtifact()) {
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
  if (mayRestoreTaskFromPriorOutput(input) && taskConfig.verifyArtifact()) {
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
    requiredMarkers: requiredMarkersForTask(input),
    artifactVerification: () => taskConfig.verifyArtifact() ? [taskConfig.label] : [],
  };
}

function mayRestoreTaskFromPriorOutput(input: TaskDispatchFactoryInput): boolean {
  return !wasResetByWorkflowPolicy(input);
}

function wasResetByWorkflowPolicy(input: TaskDispatchFactoryInput): boolean {
  return input.ctx.requestedTask?.resetBy?.type === 'workflow-policy';
}

function createCheckAiReviewDispatchTask(input: TaskDispatchFactoryInput): DispatchableTask {
  const changeDir = input.ctx.artifactManager.getChangeDir(input.ctx.issue.number)
    || input.ctx.artifactManager.createChangeDir(input.ctx.issue.number, input.ctx.issue.title);
  if (!changeDir) throw new Error(`Failed to get or create change directory for issue #${input.ctx.issue.number}`);

  const reviewOutputPath = 'review.md';
  const completedSteps = input.ctx.checkpointManager.getResumeSteps(input.ctx.issue.number, 'check');
  if (mayRestoreTaskFromPriorOutput(input) && completedSteps.includes(input.task.taskId) && fs.existsSync(path.join(changeDir, reviewOutputPath))) {
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
    requiredMarkers: requiredMarkersForTask(input),
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

function buildWorkflowServiceFn(
  uses: string,
  input: TaskDispatchFactoryInput,
  integrator: OpenSpecIntegrator,
): ((ctx: StageContext) => Promise<unknown>) | null {
  if (uses === 'mohist/openspec-sync') {
    return async (ctx) => {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      if (!changeDir) throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
      const summary = await integrator.apply(changeDir, input.worktreePath);
      return {
        step: input.task.taskId,
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

  if (uses === 'mohist/archive-change') {
    return async (ctx) => {
      const changeDir = ctx.artifactManager.getChangeDir(ctx.issue.number);
      if (!changeDir) throw new Error(`Change directory not found for issue #${ctx.issue.number}`);
      await ctx.artifactManager.archiveChange(ctx.issue.number);
      return { step: input.task.taskId, archivePath: path.relative(input.worktreePath, changeDir), success: true };
    };
  }

  if (uses === 'mohist/merge') {
    return async (ctx) => {
      const project = ctx.projectRepo?.findById(ctx.issue.projectId);
      if (!project) throw new Error(`Project not found: ${ctx.issue.projectId}`);
      const baseBranch = project.baseBranch;

      if (ctx.issue.mergeState === MergeState.Merged) {
        const delivery = recoverMergeDelivery(ctx, input.task.taskId, baseBranch);
        if (!delivery) throw new Error('Issue is already marked merged but merge delivery evidence is missing');
        return { step: input.task.taskId, ...delivery, skipped: true, reason: 'already-merged' };
      }
      if (!ctx.worktreeManager.mergeApprovedCandidate) throw new Error('worktreeManager.mergeApprovedCandidate is not available');

      const mergeTruth = await ctx.worktreeManager.mergeApprovedCandidate(project.path, project.name, ctx.issue.number, baseBranch);
      if ('failingStep' in mergeTruth) {
        throw new Error(`Merge failed at ${mergeTruth.failingStep}: ${mergeTruth.error}` + (mergeTruth.conflictFiles?.length ? ` Conflicting files: ${mergeTruth.conflictFiles.join(', ')}` : ''));
      }
      if (ctx.issueRepo.setMergeState) ctx.issueRepo.setMergeState(ctx.issue.id, MergeState.Merged);
      return {
        step: input.task.taskId,
        targetBranch: mergeTruth.targetBranch,
        baseSha: mergeTruth.baseSha,
        candidateHeadSha: mergeTruth.candidateHeadSha,
        landedSha: mergeTruth.landedSha,
        rebased: mergeTruth.rebased,
      };
    };
  }

  return null;
}

function recoverMergeDelivery(ctx: StageContext, taskId: string, targetBranch: string): { targetBranch: string; baseSha?: string; candidateHeadSha?: string; landedSha: string; rebased?: boolean } | null {
  const stageRun = ctx.workflowRun?.stageRuns.find(candidate => candidate.stage === ctx.issue.stage);
  const mergeTask = stageRun?.tasks.find(task => task.taskId === taskId && task.status === 'completed');
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
