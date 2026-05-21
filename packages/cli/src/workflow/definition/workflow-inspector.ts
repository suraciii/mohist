import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';
import {
  MOHIST_DEFAULT_WORKFLOW_DEFINITION,
  MOHIST_DEFAULT_WORKFLOW_YAML,
} from './default-workflow';
import {
  cloneWorkflowDefinition,
  createWorkflowDefinitionSnapshot,
  type CheckDefinition,
  type CheckFailurePolicy,
  type CompiledStageDefinition,
  type StageDefinition,
  type TaskDefinition,
  type WorkflowDefinition,
  type WorkflowDefinitionSnapshot,
  type WorkflowStageId,
} from '../model';
import { compileRuntimeWorkflowDefinitionSnapshot } from '../runner/workflow-runtime-definition';
import {
  parseWorkflowDefinitionSource,
  type WorkflowSourceDefinition,
} from './workflow-definition-source';
import {
  getWorkflowUseDefinition,
  inferWorkflowCheckUse,
  inferWorkflowTaskUse,
  isWorkflowUseAllowed,
} from '../uses-catalog';

export type WorkflowDiagnosticSeverity = 'error' | 'warning';

export interface WorkflowDiagnostic {
  severity: WorkflowDiagnosticSeverity;
  path: string;
  message: string;
  suggestion?: string;
}

export interface ResolvedWorkflowDefinition {
  snapshot: WorkflowDefinitionSnapshot;
  sourceChain: string[];
  diagnostics: WorkflowDiagnostic[];
}

export type ExplainedWorkflowItem =
  | {
    kind: 'task';
    stage: WorkflowStageId;
    id: string;
    title: string;
    source: string;
    uses: string;
    dependsOn: string[];
    requiredMarkers?: number;
    selfRepair?: boolean;
    useDescription?: string;
  }
  | {
    kind: 'check';
    stage: WorkflowStageId;
    id: string;
    title: string;
    source: string;
    uses: string;
    phase: string;
    blocking: boolean;
    reaction?: CheckFailurePolicy;
    inputs?: Record<string, unknown>;
    useDescription?: string;
  };

type WorkflowOverrideDocument = Record<string, unknown>;

export function getBuiltinDefaultWorkflowYaml(): string {
  return MOHIST_DEFAULT_WORKFLOW_YAML;
}

export function resolveWorkflowDefinition(cwd: string = process.cwd()): ResolvedWorkflowDefinition {
  const overridePath = findWorkflowOverridePath(cwd);
  if (!overridePath) return resolveBuiltinDefault();

  const parsed = parseWorkflowOverride(overridePath);
  if (parsed.diagnostics.some(diagnostic => diagnostic.severity === 'error')) {
    return {
      snapshot: createRuntimeSnapshot({
        definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
        source: { type: 'builtin', id: MOHIST_DEFAULT_WORKFLOW_DEFINITION.id },
      }),
      sourceChain: ['mohist/default', overridePath],
      diagnostics: parsed.diagnostics,
    };
  }

  if (isFullCustomWorkflowDocument(parsed.document)) {
    const compiled = compileFullCustomWorkflow(parsed.document, overridePath);
    return {
      snapshot: createRuntimeSnapshot({
        definition: compiled.definition,
        source: { type: 'project', path: overridePath },
      }),
      sourceChain: [overridePath],
      diagnostics: [...parsed.diagnostics, ...compiled.diagnostics],
    };
  }

  const definition = cloneWorkflowDefinition(MOHIST_DEFAULT_WORKFLOW_DEFINITION);
  const diagnostics = applyWorkflowOverride(definition, parsed.document, overridePath);
  return {
    snapshot: createRuntimeSnapshot({
      definition,
      source: { type: 'project', path: overridePath },
    }),
    sourceChain: ['mohist/default', overridePath],
    diagnostics: [...parsed.diagnostics, ...diagnostics],
  };
}

function isFullCustomWorkflowDocument(document: WorkflowOverrideDocument): boolean {
  return isRecord(document.workflow) && Array.isArray(document.workflow.stages);
}

function compileFullCustomWorkflow(
  document: WorkflowOverrideDocument,
  filePath: string,
): { definition: WorkflowDefinition; diagnostics: WorkflowDiagnostic[] } {
  const diagnostics: WorkflowDiagnostic[] = [];
  const workflow = isRecord(document.workflow) ? document.workflow : {};
  const id = typeof workflow.id === 'string' && workflow.id.trim().length > 0
    ? workflow.id
    : 'project/custom';
  const name = typeof workflow.name === 'string' ? workflow.name : undefined;
  const artifacts = compileWorkflowArtifacts(workflow.artifacts, filePath, diagnostics);
  const rawStages = Array.isArray(workflow.stages) ? workflow.stages : [];
  const stages: WorkflowSourceDefinition['stages'] = [];

  for (const [stageIndex, rawStage] of rawStages.entries()) {
    const stagePath = `${filePath}:workflow.stages[${stageIndex}]`;
    if (!isRecord(rawStage)) {
      diagnostics.push({ severity: 'error', path: stagePath, message: 'stage must be a mapping' });
      continue;
    }

    const stage = parseStageId(rawStage.id ?? rawStage.stage);
    if (!stage) {
      diagnostics.push({
        severity: 'error',
        path: `${stagePath}.id`,
        message: 'stage id must be a non-empty string',
      });
      continue;
    }

    const tasks = compileCustomTasks(rawStage.tasks, stagePath, diagnostics);
    const checks = compileCustomChecks(rawStage.checks, stagePath, diagnostics);
    const approval = rawStage.approval === true;
    const on = compileStageEventPolicies(rawStage.on, stagePath, diagnostics);
    const tasksFrom = compileTasksFrom(rawStage.tasksFrom, stagePath, diagnostics);

    stages.push({
      id: stage,
      tasks,
      tasksFrom,
      checks,
      on,
      approval,
    });
  }

  return {
    definition: parseWorkflowDefinitionSource({ id, name, artifacts, stages }, { taskSource: 'project', checkSource: 'project' }),
    diagnostics,
  };
}

function compileWorkflowArtifacts(
  rawArtifacts: unknown,
  filePath: string,
  diagnostics: WorkflowDiagnostic[],
): Record<string, string> | undefined {
  if (rawArtifacts === undefined) return undefined;
  if (!isRecord(rawArtifacts)) {
    diagnostics.push({ severity: 'error', path: `${filePath}:workflow.artifacts`, message: 'artifacts must be a mapping of name to path template' });
    return undefined;
  }
  const artifacts: Record<string, string> = {};
  for (const [name, value] of Object.entries(rawArtifacts)) {
    if (typeof value !== 'string') {
      diagnostics.push({ severity: 'error', path: `${filePath}:workflow.artifacts.${name}`, message: 'artifact value must be a string path template' });
      continue;
    }
    artifacts[name] = value;
  }
  return Object.keys(artifacts).length > 0 ? artifacts : undefined;
}

function compileCustomTasks(
  rawTasks: unknown,
  stagePath: string,
  diagnostics: WorkflowDiagnostic[],
): TaskDefinition[] {
  if (rawTasks === undefined) return [];
  if (!Array.isArray(rawTasks)) {
    diagnostics.push({ severity: 'error', path: `${stagePath}.tasks`, message: 'tasks must be a list' });
    return [];
  }

  const tasks: TaskDefinition[] = [];
  for (const [taskIndex, rawTask] of rawTasks.entries()) {
    const taskPath = `${stagePath}.tasks[${taskIndex}]`;
    if (!isRecord(rawTask) || typeof rawTask.id !== 'string' || typeof rawTask.uses !== 'string') {
      diagnostics.push({ severity: 'error', path: taskPath, message: 'task requires id and uses' });
      continue;
    }
    if (!isWorkflowUseAllowed(rawTask.uses, 'task')) {
      diagnostics.push({ severity: 'error', path: `${taskPath}.uses`, message: `Use '${rawTask.uses}' is not allowed as a task` });
      continue;
    }
    if (!isExecutableCustomTaskUse(rawTask.uses)) {
      diagnostics.push({ severity: 'error', path: `${taskPath}.uses`, message: `Use '${rawTask.uses}' is not supported for full custom task execution yet` });
      continue;
    }
    if ('emits' in rawTask) {
      diagnostics.push({ severity: 'error', path: `${taskPath}.emits`, message: 'task.emits is not supported; use onSuccess.emit for unconditional success events or let the task runtime raise events' });
      continue;
    }
    const task: TaskDefinition = {
      id: rawTask.id,
      title: typeof rawTask.title === 'string' ? rawTask.title : rawTask.id,
      source: 'project',
      uses: rawTask.uses,
      with: isRecord(rawTask.with) ? { ...rawTask.with } : undefined,
      onSuccess: compileTaskSuccessAction(rawTask.onSuccess),
      dependsOn: arrayValue(rawTask.dependsOn ?? rawTask.needs).filter((value): value is string => typeof value === 'string'),
      resultContract: isRecord(rawTask.resultContract) ? rawTask.resultContract as unknown as TaskDefinition['resultContract'] : undefined,
    };
    if (task.uses === 'mohist/agent' && !hasAgentPromptSource(task.with)) {
      diagnostics.push({
        severity: 'error',
        path: `${taskPath}.with.prompt`,
        message: `Agent task '${task.id}' requires with.prompt ref/file/inline or with.promptFile`,
      });
    }
    tasks.push(task);
  }
  return tasks;
}

function isExecutableCustomTaskUse(uses: string): boolean {
  return uses === 'mohist/agent'
    || uses === 'mohist/check/ai-review'
    || uses === 'mohist/ralph-tasks'
    || uses === 'mohist/openspec-sync'
    || uses === 'mohist/archive-change'
    || uses === 'mohist/merge'
    || uses === 'mohist/rebase';
}

function compileTasksFrom(
  rawTasksFrom: unknown,
  stagePath: string,
  diagnostics: WorkflowDiagnostic[],
): WorkflowSourceDefinition['stages'][number]['tasksFrom'] | undefined {
  if (rawTasksFrom === undefined) return undefined;
  if (typeof rawTasksFrom === 'string') {
    const use = getWorkflowUseDefinition(rawTasksFrom);
    if (use && use.allowedPlacement === 'task' && use.sourceKind) return rawTasksFrom;
  }
  diagnostics.push({
    severity: 'error',
    path: `${stagePath}.tasksFrom`,
    message: 'tasksFrom must reference a workflow task source use',
  });
  return undefined;
}

function compileCustomChecks(
  rawChecks: unknown,
  stagePath: string,
  diagnostics: WorkflowDiagnostic[],
): CheckDefinition[] {
  if (rawChecks === undefined) return [];
  if (!Array.isArray(rawChecks)) {
    diagnostics.push({ severity: 'error', path: `${stagePath}.checks`, message: 'checks must be a list' });
    return [];
  }

  const checks: CheckDefinition[] = [];
  for (const [checkIndex, rawCheck] of rawChecks.entries()) {
    const checkPath = `${stagePath}.checks[${checkIndex}]`;
    if (!isRecord(rawCheck) || typeof rawCheck.id !== 'string') {
      diagnostics.push({ severity: 'error', path: checkPath, message: 'check requires id' });
      continue;
    }
    const uses = typeof rawCheck.uses === 'string' ? rawCheck.uses : inferWorkflowCheckUse(rawCheck.id);
    if (!isWorkflowUseAllowed(uses, 'check')) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.uses`, message: `Use '${rawCheck.uses}' is not allowed as a check` });
      continue;
    }
    if (uses === 'mohist/shell' && (!isRecord(rawCheck.with) || typeof rawCheck.with.command !== 'string')) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.with.command`, message: `Shell check '${rawCheck.id}' requires with.command` });
    }
    if (typeof rawCheck.uses === 'string' && uses === 'mohist/artifact-exists' && (!isRecord(rawCheck.with) || typeof rawCheck.with.path !== 'string')) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.with.path`, message: `Artifact check '${rawCheck.id}' requires with.path` });
    }
    const check: CheckDefinition = {
      name: rawCheck.id,
      title: typeof rawCheck.title === 'string' ? rawCheck.title : rawCheck.id,
      source: 'project',
      uses: typeof rawCheck.uses === 'string' ? rawCheck.uses : undefined,
      with: isRecord(rawCheck.with) ? { ...rawCheck.with } : undefined,
    };
    const onFailure = compileCheckOnFailure(rawCheck.onFailure, checkPath, diagnostics);
    if (onFailure) check.onFailure = onFailure;
    checks.push(check);
  }
  return checks;
}

function compileCheckOnFailure(
  rawOnFailure: unknown,
  checkPath: string,
  diagnostics: WorkflowDiagnostic[],
): CheckDefinition['onFailure'] | undefined {
  if (rawOnFailure === undefined) return undefined;
  if (!isRecord(rawOnFailure) || !isRecord(rawOnFailure.retry)) {
    diagnostics.push({ severity: 'error', path: `${checkPath}.onFailure`, message: 'onFailure requires retry' });
    return undefined;
  }
  const retry = rawOnFailure.retry;
  const rawTask = retry.task;
  if (typeof retry.limit !== 'number') {
    diagnostics.push({ severity: 'error', path: `${checkPath}.onFailure.retry.limit`, message: 'retry.limit must be a number' });
    return undefined;
  }
  if (!isRecord(rawTask) || typeof rawTask.id !== 'string') {
    diagnostics.push({ severity: 'error', path: `${checkPath}.onFailure.retry.task`, message: 'retry.task requires id' });
    return undefined;
  }
  if ('emits' in rawTask) {
    diagnostics.push({ severity: 'error', path: `${checkPath}.onFailure.retry.task.emits`, message: 'task.emits is not supported; use onSuccess.emit for unconditional success events or let the task runtime raise events' });
    return undefined;
  }
  const task: TaskDefinition = {
    id: rawTask.id,
    title: typeof rawTask.title === 'string' ? rawTask.title : rawTask.id,
    source: 'project',
    uses: typeof rawTask.uses === 'string' ? rawTask.uses : 'mohist/agent',
    with: isRecord(rawTask.with) ? { ...rawTask.with } : undefined,
    onSuccess: compileTaskSuccessAction(rawTask.onSuccess),
  };
  return {
    retry: {
      limit: retry.limit,
      task,
      inputFrom: compileReactionInputs(retry.inputFrom),
    },
  };
}

function compileTaskSuccessAction(rawOnSuccess: unknown): TaskDefinition['onSuccess'] | undefined {
  if (!isRecord(rawOnSuccess)) return undefined;
  const emit = arrayValue(rawOnSuccess.emit).filter((value): value is string => typeof value === 'string');
  return emit.length > 0 ? { emit } : undefined;
}

function compileStageEventPolicies(
  rawOn: unknown,
  stagePath: string,
  diagnostics: WorkflowDiagnostic[],
): StageDefinition['on'] | undefined {
  if (rawOn === undefined) return undefined;
  if (!isRecord(rawOn)) {
    diagnostics.push({ severity: 'error', path: `${stagePath}.on`, message: 'on must be a mapping keyed by event name' });
    return undefined;
  }
  const on: NonNullable<StageDefinition['on']> = {};
  for (const [eventName, rawPolicy] of Object.entries(rawOn)) {
    if (!isRecord(rawPolicy)) {
      diagnostics.push({ severity: 'error', path: `${stagePath}.on.${eventName}`, message: 'event policy must be a mapping' });
      continue;
    }
    if (!isRecord(rawPolicy.reset)) {
      diagnostics.push({ severity: 'error', path: `${stagePath}.on.${eventName}.reset`, message: 'reset must be a mapping with at least one target' });
      continue;
    }
    const tasks = arrayValue(rawPolicy.reset.tasks).filter((value): value is string => typeof value === 'string');
    let checks: 'all' | string[] | undefined;
    if (rawPolicy.reset.checks === 'all') {
      checks = 'all';
    } else {
      const checkList = arrayValue(rawPolicy.reset.checks).filter((value): value is string => typeof value === 'string');
      if (checkList.length > 0) checks = checkList;
    }
    const approval = typeof rawPolicy.reset.approval === 'boolean' ? rawPolicy.reset.approval : undefined;
    if (tasks.length === 0 && checks === undefined && approval !== true) {
      diagnostics.push({ severity: 'error', path: `${stagePath}.on.${eventName}.reset`, message: 'reset must target tasks, checks, or approval' });
      continue;
    }
    on[eventName] = {
      reset: {
        tasks: tasks.length > 0 ? tasks : undefined,
        checks,
        approval,
      },
    };
  }
  return Object.keys(on).length > 0 ? on : undefined;
}

function compileReactionInputs(rawInputs: unknown): CheckFailurePolicy['inputFrom'] {
  if (!Array.isArray(rawInputs)) return undefined;
  return rawInputs
    .filter(isRecord)
    .map(input => ({ ...input })) as CheckFailurePolicy['inputFrom'];
}

function parseStageId(value: unknown): WorkflowStageId | null {
  if (typeof value !== 'string') return null;
  const stage = value.trim();
  return stage.length > 0 ? stage : null;
}

function resolveBuiltinDefault(): ResolvedWorkflowDefinition {
  return {
    snapshot: createRuntimeSnapshot({
      definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
      source: { type: 'builtin', id: MOHIST_DEFAULT_WORKFLOW_DEFINITION.id },
    }),
    sourceChain: ['mohist/default'],
    diagnostics: [],
  };
}

function createRuntimeSnapshot(input: Parameters<typeof createWorkflowDefinitionSnapshot>[0]): WorkflowDefinitionSnapshot {
  return compileRuntimeWorkflowDefinitionSnapshot(createWorkflowDefinitionSnapshot(input));
}

function findWorkflowOverridePath(cwd: string): string | null {
  const candidates = [
    path.join(cwd, '.mohist', 'workflow.yaml'),
    path.join(cwd, 'workflow.yaml'),
  ];
  return candidates.find(candidate => fs.existsSync(candidate)) ?? null;
}

function parseWorkflowOverride(filePath: string): { document: WorkflowOverrideDocument; diagnostics: WorkflowDiagnostic[] } {
  try {
    const raw = yaml.parse(fs.readFileSync(filePath, 'utf-8'));
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
      return {
        document: {},
        diagnostics: [{
          severity: 'error',
          path: filePath,
          message: 'Workflow override must be a mapping',
        }],
      };
    }
    return { document: raw as WorkflowOverrideDocument, diagnostics: [] };
  } catch (err) {
    return {
      document: {},
      diagnostics: [{
        severity: 'error',
        path: filePath,
        message: `Cannot parse workflow override: ${err instanceof Error ? err.message : String(err)}`,
      }],
    };
  }
}

function applyWorkflowOverride(definition: WorkflowDefinition, document: WorkflowOverrideDocument, filePath: string): WorkflowDiagnostic[] {
  const diagnostics: WorkflowDiagnostic[] = [];
  if (document.extends !== 'mohist/default') {
    diagnostics.push({
      severity: 'error',
      path: `${filePath}:extends`,
      message: 'Only extends: mohist/default is supported',
      suggestion: 'Use extends: mohist/default for safe project overrides.',
    });
    return diagnostics;
  }

  applyTopLevelChecks(definition, document.checks, filePath, diagnostics);
  applyStageOverrides(definition, document.stages, filePath, diagnostics);
  return diagnostics;
}

function applyTopLevelChecks(
  definition: WorkflowDefinition,
  rawChecks: unknown,
  filePath: string,
  diagnostics: WorkflowDiagnostic[],
): void {
  if (!rawChecks) return;
  if (!isRecord(rawChecks)) {
    diagnostics.push({ severity: 'error', path: `${filePath}:checks`, message: 'checks must be a mapping' });
    return;
  }

  for (const [checkName, raw] of Object.entries(rawChecks)) {
    if (!isRecord(raw)) {
      diagnostics.push({ severity: 'error', path: `${filePath}:checks.${checkName}`, message: 'check override must be a mapping' });
      continue;
    }
    const target = findCheck(definition, checkName);
    if (!target) {
      diagnostics.push({ severity: 'error', path: `${filePath}:checks.${checkName}`, message: `Unknown builtin check '${checkName}'` });
      continue;
    }
    applyCheckOverride(target.stage, target.check, raw, `${filePath}:checks.${checkName}`, diagnostics);
  }
}

function applyStageOverrides(
  definition: WorkflowDefinition,
  rawStages: unknown,
  filePath: string,
  diagnostics: WorkflowDiagnostic[],
): void {
  if (!rawStages) return;
  if (!isRecord(rawStages)) {
    diagnostics.push({ severity: 'error', path: `${filePath}:stages`, message: 'stages must be a mapping keyed by stage name' });
    return;
  }

  for (const [stageName, raw] of Object.entries(rawStages)) {
    const stage = definition.stages.find(candidate => candidate.stage === stageName);
    const stagePath = `${filePath}:stages.${stageName}`;
    if (!stage) {
      diagnostics.push({ severity: 'error', path: stagePath, message: `Unsupported stage '${stageName}'` });
      continue;
    }
    if (!isRecord(raw)) {
      diagnostics.push({ severity: 'error', path: stagePath, message: 'stage override must be a mapping' });
      continue;
    }

    if ('approval' in raw) {
      if (typeof raw.approval !== 'boolean') {
        diagnostics.push({ severity: 'error', path: `${stagePath}.approval`, message: 'approval must be true or false' });
      } else {
        stage.requiresApproval = raw.approval;
      }
    }

    applyDisableList(stage, raw.disable, stagePath, diagnostics);
    applyStageChecks(stage, raw.checks, stagePath, diagnostics);
  }
}

function applyDisableList(
  stage: StageDefinition,
  rawDisable: unknown,
  stagePath: string,
  diagnostics: WorkflowDiagnostic[],
): void {
  if (!rawDisable) return;
  const values = Array.isArray(rawDisable)
    ? rawDisable
    : isRecord(rawDisable)
      ? [...arrayValue(rawDisable.tasks), ...arrayValue(rawDisable.checks)]
      : null;
  if (!values) {
    diagnostics.push({ severity: 'error', path: `${stagePath}.disable`, message: 'disable must be a list or tasks/checks mapping' });
    return;
  }

  for (const value of values) {
    if (typeof value !== 'string') {
      diagnostics.push({ severity: 'error', path: `${stagePath}.disable`, message: 'disable entries must be strings' });
      continue;
    }
    const taskCount = stage.tasks.length;
    const checkCount = stage.checks.length;
    stage.tasks = stage.tasks.filter(task => task.id !== value);
    stage.checks = stage.checks.filter(check => check.name !== value);
    if (stage.tasks.length === taskCount && stage.checks.length === checkCount) {
      diagnostics.push({ severity: 'error', path: `${stagePath}.disable`, message: `Cannot disable unknown task or check '${value}'` });
    }
  }
}

function applyStageChecks(
  stage: StageDefinition,
  rawChecks: unknown,
  stagePath: string,
  diagnostics: WorkflowDiagnostic[],
): void {
  if (!rawChecks) return;
  if (!Array.isArray(rawChecks)) {
    diagnostics.push({ severity: 'error', path: `${stagePath}.checks`, message: 'stage checks must be a list' });
    return;
  }
  for (const [index, raw] of rawChecks.entries()) {
    const checkPath = `${stagePath}.checks[${index}]`;
    if (!isRecord(raw) || typeof raw.id !== 'string' || typeof raw.uses !== 'string') {
      diagnostics.push({ severity: 'error', path: checkPath, message: 'project check requires id and uses' });
      continue;
    }
    if (!isWorkflowUseAllowed(raw.uses, 'check')) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.uses`, message: `Use '${raw.uses}' is not allowed as a check` });
      continue;
    }
    if (!isExecutableCustomCheckUse(raw.uses)) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.uses`, message: `Use '${raw.uses}' is not supported for full custom check execution yet` });
      continue;
    }
    stage.checks.push({
      name: raw.id,
      title: typeof raw.title === 'string' ? raw.title : raw.id,
      source: 'project',
      uses: raw.uses,
      with: isRecord(raw.with) ? { ...raw.with } : undefined,
      onFailure: compileCheckOnFailure(raw.onFailure, checkPath, diagnostics),
    });
  }
}

function applyCheckOverride(
  _stage: StageDefinition,
  check: CheckDefinition,
  raw: Record<string, unknown>,
  checkPath: string,
  diagnostics: WorkflowDiagnostic[],
): void {
  if (raw.uses !== undefined && (typeof raw.uses !== 'string' || !isWorkflowUseAllowed(raw.uses, 'check'))) {
    diagnostics.push({ severity: 'error', path: `${checkPath}.uses`, message: `Use '${String(raw.uses)}' is not allowed as a check` });
    return;
  }
  if (typeof raw.uses === 'string' && !isExecutableCustomCheckUse(raw.uses)) {
    diagnostics.push({ severity: 'error', path: `${checkPath}.uses`, message: `Use '${raw.uses}' is not supported for full custom check execution yet` });
    return;
  }
  check.source = 'project';
  if (typeof raw.uses === 'string') check.uses = raw.uses;
  if (isRecord(raw.with)) check.with = { ...raw.with };
  if (isRecord(raw.onFailure)) {
    const nextOnFailure = compileCheckOnFailure(raw.onFailure, `${checkPath}.onFailure`, diagnostics);
    if (nextOnFailure) check.onFailure = nextOnFailure;
  }
}

export function validateWorkflowDefinition(resolved: ResolvedWorkflowDefinition = resolveWorkflowDefinition()): WorkflowDiagnostic[] {
  const diagnostics: WorkflowDiagnostic[] = [...resolved.diagnostics];
  const seenStages = new Set<WorkflowStageId>();

  for (const [stageIndex, stage] of resolved.snapshot.compiledStageDefinitions.entries()) {
    const stagePath = `stages[${stageIndex}]`;
    if (seenStages.has(stage.stage)) {
      diagnostics.push({
        severity: 'error',
        path: `${stagePath}.stage`,
        message: `Duplicate stage '${stage.stage}'`,
        suggestion: 'Keep one definition for each workflow stage.',
      });
    }
    seenStages.add(stage.stage);

    const taskIds = new Set(stage.tasks.map(task => task.id));
    for (const [taskIndex, task] of stage.tasks.entries()) {
      if (!task.id.trim()) {
        diagnostics.push({ severity: 'error', path: `${stagePath}.tasks[${taskIndex}].id`, message: 'Task id is required' });
      }
      const uses = task.uses ?? inferTaskUseForStage(stage, task.id);
      if (!isWorkflowUseAllowed(uses, 'task')) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.tasks[${taskIndex}].uses`,
          message: `Use '${uses}' is not allowed as a task`,
        });
      }
      if (uses === 'mohist/agent' && task.source === 'project' && !hasAgentPromptSource(task.with)) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.tasks[${taskIndex}].with.prompt`,
          message: `Agent task '${task.id}' requires with.prompt ref/file/inline or with.promptFile`,
        });
      }
      for (const dependency of task.dependsOn ?? []) {
        if (!taskIds.has(dependency)) {
          diagnostics.push({
            severity: 'error',
            path: `${stagePath}.tasks[${taskIndex}].dependsOn`,
            message: `Task '${task.id}' depends on unknown task '${dependency}'`,
            suggestion: 'Use a task id declared in the same stage.',
          });
        }
      }
    }

    const checkNames = new Set(stage.checks.map(check => check.name));
    for (const [checkIndex, check] of stage.checks.entries()) {
      const uses = check.uses ?? inferWorkflowCheckUse(check.name);
      if (!isWorkflowUseAllowed(uses, 'check')) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.checks[${checkIndex}].uses`,
          message: `Use '${uses}' is not allowed as a check`,
        });
      }
      if (!isExecutableCustomCheckUse(uses)) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.checks[${checkIndex}].uses`,
          message: `Use '${uses}' is not supported for full custom check execution yet`,
        });
      }
      if (uses === 'mohist/shell' && (!check.with || typeof check.with.command !== 'string' || check.with.command.length === 0)) {
        diagnostics.push({
          severity: 'error',
          path: `${stagePath}.checks[${checkIndex}].with.command`,
          message: `Shell check '${check.name}' requires with.command`,
        });
      }
    }
    for (const policy of stage.checkPolicies ?? []) {
      if (!checkNames.has(policy.checkName)) {
        diagnostics.push({ severity: 'error', path: `${stagePath}.checkPolicies`, message: `Check policy references unknown check '${policy.checkName}'` });
      }
    }
  }

  return diagnostics;
}

function isExecutableCustomCheckUse(uses: string): boolean {
  return uses === 'mohist/artifact-exists'
    || uses === 'mohist/marker'
    || uses === 'mohist/verdict'
    || uses === 'mohist/health-gate'
    || uses === 'mohist/merge-ready'
    || uses === 'mohist/shell'
    || uses === 'mohist/approval';
}

export function explainWorkflowItem(
  itemId: string,
  resolved: ResolvedWorkflowDefinition = resolveWorkflowDefinition(),
): ExplainedWorkflowItem | null {
  for (const stage of resolved.snapshot.compiledStageDefinitions) {
    const task = stage.tasks.find(candidate => candidate.id === itemId);
    if (task) return explainTask(stage, task);

    const check = stage.checks.find(candidate => candidate.name === itemId);
    if (check) return explainCheck(stage, check);
  }
  return null;
}

function explainTask(stage: CompiledStageDefinition, task: TaskDefinition): ExplainedWorkflowItem {
  const uses = task.uses ?? inferWorkflowTaskUse(task.id);
  const useDefinition = getWorkflowUseDefinition(uses);
  return {
    kind: 'task',
    stage: stage.stage,
    id: task.id,
    title: task.title,
    source: task.source ?? 'builtin',
    uses,
    dependsOn: task.dependsOn ?? [],
    requiredMarkers: Array.isArray(task.with?.requiredMarkers) ? task.with.requiredMarkers.length : undefined,
    useDescription: useDefinition?.description,
  };
}

function explainCheck(stage: CompiledStageDefinition, check: CheckDefinition): ExplainedWorkflowItem {
  const phase = stage.checkPolicies?.find(candidate => candidate.checkName === check.name)?.phase ?? 'post-task';
  const reaction = stage.checkFailurePolicies?.find(candidate => candidate.checkName === check.name);
  const uses = check.uses ?? inferWorkflowCheckUse(check.name);
  const useDefinition = getWorkflowUseDefinition(uses);
  return {
    kind: 'check',
    stage: stage.stage,
    id: check.name,
    title: check.title,
    source: check.source ?? 'builtin',
    uses,
    phase,
    blocking: true,
    reaction,
    inputs: check.with,
    useDescription: useDefinition?.description,
  };
}

function findCheck(definition: WorkflowDefinition, checkName: string): { stage: StageDefinition; check: CheckDefinition } | null {
  for (const stage of definition.stages) {
    const check = stage.checks.find(candidate => candidate.name === checkName);
    if (check) return { stage, check };
  }
  return null;
}

function inferTaskUseForStage(stage: CompiledStageDefinition, taskId: string): string {
  const task = stage.tasks.find(candidate => candidate.id === taskId);
  return task?.uses ?? inferWorkflowTaskUse(taskId);
}

function hasAgentPromptSource(withConfig: Record<string, unknown> | undefined): boolean {
  if (!withConfig) return false;
  if (typeof withConfig.promptFile === 'string' && withConfig.promptFile.trim().length > 0) return true;
  if (typeof withConfig.prompt === 'string' && withConfig.prompt.trim().length > 0) return true;
  if (!isRecord(withConfig.prompt)) return false;
  const prompt = withConfig.prompt;
  return (typeof prompt.ref === 'string' && prompt.ref.trim().length > 0)
    || (typeof prompt.file === 'string' && prompt.file.trim().length > 0)
    || (typeof prompt.inline === 'string' && prompt.inline.trim().length > 0);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value));
}

function arrayValue(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}
