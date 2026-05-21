import * as fs from 'fs';
import * as path from 'path';
import { MergeState } from '../../types';
import type { StageContext, CheckResult, StageTaskResult } from '../stage-context';
import { emitStageTaskUpdate } from '../stage-context';
import type { AgentSessionTaskHandler, AgentSessionTaskInput, ExecutableTask } from './types';
import type { TaskDefinition } from '../model';
import type { RequiredMarkerDefinition } from './agent-required-markers';
import { createAgentSessionTaskHandler } from './agent-session-task-handler';
import { createServiceCallTaskHandler } from './service-call-task-handler';
import { workflowDefinitionSnapshotFromUnknown } from '../projection/workflow-run-snapshot';
import { createWorkflowTemplateContext, renderWorkflowTemplate } from '../template';
import { executeRebaseBranchTask } from './rebase-task-handler';
import { buildReviewerPrompt } from '../../agents/artifact-prompt';
import { OpenSpecIntegrator } from '../../openspec/open-spec-integrator';

type AgentPromptSource =
  | { file: string }
  | { inline: string };

export interface TaskDispatchFactoryInput {
  ctx: StageContext;
  task: ExecutableTask;
  attempt: number;
  failedCheck?: CheckResult;
  worktreePath: string;
  agentSessionRef?: string;
  sourceTask?: TaskDefinition;
}

export interface TaskDispatchProvider {
  id: string;
  run(input: TaskDispatchFactoryInput): Promise<StageTaskResult | null>;
}

export interface DefaultTaskDispatchProviderOverrides {
  rebase?: TaskDispatchProvider['run'];
  openspecSync?: TaskDispatchProvider['run'];
  archiveChange?: TaskDispatchProvider['run'];
  merge?: TaskDispatchProvider['run'];
}

export interface DefaultTaskDispatchFactoryRegistryOptions {
  agentSessionHandler?: AgentSessionTaskHandler;
  overrides?: DefaultTaskDispatchProviderOverrides;
  readFile?: (path: string, encoding: BufferEncoding) => string;
}

export interface TaskDispatchFactoryRegistry {
  run(input: TaskDispatchFactoryInput): Promise<StageTaskResult | null>;
  get(id: string): TaskDispatchProvider | undefined;
  register(provider: TaskDispatchProvider): void;
}

export function createTaskDispatchFactoryRegistry(providers: TaskDispatchProvider[]): TaskDispatchFactoryRegistry {
  const map = new Map<string, TaskDispatchProvider>();
  for (const provider of providers) {
    map.set(provider.id, provider);
  }

  return {
    run(input) {
      const providerId = resolveTaskProviderId(input);
      return map.get(providerId)?.run(input) ?? Promise.resolve(null);
    },
    get(id) {
      return map.get(id);
    },
    register(provider) {
      map.set(provider.id, provider);
    },
  };
}

export function createDefaultTaskDispatchFactoryRegistry(options: DefaultTaskDispatchFactoryRegistryOptions = {}): TaskDispatchFactoryRegistry {
  const integrator = new OpenSpecIntegrator();
  const agentSessionHandler = options.agentSessionHandler ?? createAgentSessionTaskHandler();
  const readFile = options.readFile ?? ((filePath, encoding) => fs.readFileSync(filePath, encoding));
  return createTaskDispatchFactoryRegistry([
    {
      id: 'mohist/agent',
      run: input => runAgentSessionTask(input, agentSessionHandler, readFile),
    },
    {
      id: 'mohist/check/ai-review',
      run: input => createCheckAiReviewDispatchTask(input, agentSessionHandler),
    },
    {
      id: 'mohist/rebase',
      run: options.overrides?.rebase ?? createRebaseDispatchTask,
    },
    {
      id: 'mohist/openspec-sync',
      run: options.overrides?.openspecSync ?? (input => createServiceCallDispatchTask(input, integrator, 'mohist/openspec-sync') ?? Promise.resolve(null)),
    },
    {
      id: 'mohist/archive-change',
      run: options.overrides?.archiveChange ?? (input => createServiceCallDispatchTask(input, integrator, 'mohist/archive-change') ?? Promise.resolve(null)),
    },
    {
      id: 'mohist/merge',
      run: options.overrides?.merge ?? (input => createServiceCallDispatchTask(input, integrator, 'mohist/merge') ?? Promise.resolve(null)),
    },
  ]);
}

function resolveTaskProviderId(input: TaskDispatchFactoryInput): string {
  return input.sourceTask?.uses ?? input.task.uses ?? '';
}

function createRebaseDispatchTask(input: TaskDispatchFactoryInput): Promise<StageTaskResult> {
  return createServiceCallTaskHandler()({
    taskId: input.task.taskId,
    title: input.task.title,
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
  }, input.ctx);
}

function createServiceCallDispatchTask(input: TaskDispatchFactoryInput, integrator: OpenSpecIntegrator, uses: string): Promise<StageTaskResult> | null {
  const serviceFn = buildWorkflowServiceFn(uses, input, integrator);
  if (!serviceFn) return null;

  return createServiceCallTaskHandler()({
    taskId: input.task.taskId,
    title: input.task.title,
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    serviceFn,
  }, input.ctx);
}

function runAgentSessionTask(
  input: TaskDispatchFactoryInput,
  agentSessionHandler: AgentSessionTaskHandler,
  readFile: (path: string, encoding: BufferEncoding) => string,
): Promise<StageTaskResult> {
  const task = createAgentSessionTask(input, readFile);
  if (!task) {
    throw new Error(`Agent task '${input.task.taskId}' requires a prompt`);
  }
  return agentSessionHandler(task, input.ctx);
}

function createAgentSessionTask(
  input: TaskDispatchFactoryInput,
  readFile: (path: string, encoding: BufferEncoding) => string,
): AgentSessionTaskInput | null {
  const promptSource = agentPromptSource(input.sourceTask) ?? executableTaskPromptSource(input.task);
  if (promptSource) {
    return createGenericAgentSessionInput(input, resolveCustomAgentPrompt(input, promptSource, readFile));
  }
  if (typeof input.task.prompt === 'string' && input.task.prompt.trim().length > 0) {
    return createGenericAgentSessionInput(input, input.task.prompt);
  }
  return null;
}

function agentPromptSource(task: TaskDefinition | undefined): AgentPromptSource | null {
  const rawPrompt = task?.with?.prompt;
  if (typeof rawPrompt === 'string') return { inline: rawPrompt };
  if (typeof task?.with?.promptFile === 'string' && task.with.promptFile.trim().length > 0) {
    return { file: task.with.promptFile };
  }
  if (!rawPrompt || typeof rawPrompt !== 'object' || Array.isArray(rawPrompt)) return null;
  const prompt = rawPrompt as Record<string, unknown>;
  if (typeof prompt.file === 'string') return { file: prompt.file };
  if (typeof prompt.inline === 'string') return { inline: prompt.inline };
  return null;
}

function executableTaskPromptSource(task: ExecutableTask): AgentPromptSource | null {
  const input = task.input;
  if (!input || typeof input !== 'object' || Array.isArray(input)) return null;
  const rawPrompt = (input as Record<string, unknown>).prompt;
  if (typeof rawPrompt === 'string') return { inline: rawPrompt };
  if (!rawPrompt || typeof rawPrompt !== 'object' || Array.isArray(rawPrompt)) return null;
  const prompt = rawPrompt as Record<string, unknown>;
  if (typeof prompt.file === 'string') return { file: prompt.file };
  if (typeof prompt.inline === 'string') return { inline: prompt.inline };
  return null;
}

function resolveCustomAgentPrompt(
  input: TaskDispatchFactoryInput,
  source: AgentPromptSource,
  readFile: (path: string, encoding: BufferEncoding) => string,
): string {
  if ('inline' in source) return renderPromptTemplate(input, source.inline);
  if ('file' in source) {
    const promptPath = resolvePromptFilePath(input.worktreePath, source.file);
    return renderPromptTemplate(input, readFile(promptPath, 'utf-8'));
  }
  throw new Error(`Unsupported agent prompt source for task '${input.task.taskId}'`);
}

function resolvePromptFilePath(worktreePath: string, promptFile: string): string {
  const worktreeRoot = path.resolve(worktreePath);
  const promptPath = path.resolve(worktreeRoot, promptFile);
  if (promptPath !== worktreeRoot && !promptPath.startsWith(worktreeRoot + path.sep)) {
    throw new Error(`Agent prompt file '${promptFile}' is outside worktree`);
  }
  return promptPath;
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
  const markers = input.sourceTask?.with?.requiredMarkers ?? inputFromExecutableTask(input.task)?.requiredMarkers;
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

function createGenericAgentSessionInput(input: TaskDispatchFactoryInput, prompt: string): AgentSessionTaskInput {
  const declaredArtifacts = renderDeclaredArtifacts(input);
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    prompt,
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    agentSessionRef: input.agentSessionRef ?? sessionFromExecutableTask(input.task),
    requiredMarkers: requiredMarkersForTask(input),
    artifactVerification: () => declaredArtifacts.filter(artifact => fs.existsSync(path.resolve(input.worktreePath, artifact))),
  } satisfies AgentSessionTaskInput;
}

function renderDeclaredArtifacts(input: TaskDispatchFactoryInput): string[] {
  const rawInput = input.task.input as { artifacts?: unknown; outputs?: unknown } | undefined;
  const declaredArtifacts = extractStringArray(rawInput?.artifacts)
    ?? extractStringArray(rawInput?.outputs)
    ?? [];
  return declaredArtifacts.map(artifact => renderPromptTemplate(input, artifact));
}

function inputFromExecutableTask(task: ExecutableTask): Record<string, unknown> | undefined {
  return task.input && typeof task.input === 'object' && !Array.isArray(task.input)
    ? task.input as Record<string, unknown>
    : undefined;
}

function sessionFromExecutableTask(task: ExecutableTask): string | undefined {
  const session = inputFromExecutableTask(task)?.session;
  return typeof session === 'string' ? session : undefined;
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

function completedTaskResult(input: TaskDispatchFactoryInput, artifacts: string[], result: unknown): StageTaskResult {
  return {
    taskId: input.task.taskId,
    title: input.task.title,
    status: 'completed',
    artifacts,
    attempts: input.attempt,
    duration: 0,
    events: [],
    output: result,
  };
}

function mayRestoreTaskFromPriorOutput(input: TaskDispatchFactoryInput): boolean {
  return !wasResetByWorkflowPolicy(input);
}

function wasResetByWorkflowPolicy(input: TaskDispatchFactoryInput): boolean {
  return input.ctx.requestedTask?.resetBy?.type === 'workflow-policy';
}

async function createCheckAiReviewDispatchTask(input: TaskDispatchFactoryInput, agentSessionHandler: AgentSessionTaskHandler): Promise<StageTaskResult> {
  const changeDir = input.ctx.artifactManager.getChangeDir(input.ctx.issue.number)
    || input.ctx.artifactManager.createChangeDir(input.ctx.issue.number, input.ctx.issue.title);
  if (!changeDir) throw new Error(`Failed to get or create change directory for issue #${input.ctx.issue.number}`);

  const reviewOutputPath = 'review.md';
  const reviewOutputFullPath = path.join(changeDir, reviewOutputPath);
  const completedSteps = input.ctx.checkpointManager.getResumeSteps(input.ctx.issue.number, 'check');
  if (mayRestoreTaskFromPriorOutput(input) && completedSteps.includes(input.task.taskId) && fs.existsSync(reviewOutputFullPath)) {
    emitStageTaskUpdate(input.ctx.eventBus, input.ctx.issue.id, input.ctx.issue.projectId, input.ctx.issue.stage, input.task.taskId, input.task.title, 'completed', input.attempt, []);
    return completedTaskResult(input, [], { restoredFromCheckpoint: true });
  }

  const agentInput = {
    taskId: input.task.taskId,
    title: input.task.title,
    prompt: buildCheckReviewPrompt(input, changeDir),
    cwd: input.ctx.acpOptions.cwd ?? input.worktreePath,
    stage: input.ctx.issue.stage,
    attempt: input.attempt,
    agentSessionRef: input.agentSessionRef,
    requiredMarkers: requiredMarkersForTask(input),
    artifactVerification: () => fs.existsSync(reviewOutputFullPath) ? [reviewOutputPath] : [],
  } satisfies AgentSessionTaskInput;

  removePriorReviewOutput(reviewOutputFullPath);
  return agentSessionHandler(agentInput, input.ctx);
}

function removePriorReviewOutput(reviewOutputFullPath: string): void {
  if (!fs.existsSync(reviewOutputFullPath)) return;
  fs.rmSync(reviewOutputFullPath, { force: true });
}

function buildCheckReviewPrompt(input: TaskDispatchFactoryInput, changeDir: string): string {
  return buildReviewerPrompt(input.ctx.issue, changeDir);
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
