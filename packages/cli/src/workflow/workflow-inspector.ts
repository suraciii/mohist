import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';
import { Stage } from '../types';
import {
  MOHIST_DEFAULT_WORKFLOW_DEFINITION,
  cloneWorkflowDefinition,
  createWorkflowDefinitionSnapshot,
  parseWorkflowDefinitionSource,
  type CheckDefinition,
  type CheckFailurePolicy,
  type CompiledStageDefinition,
  type StageDefinition,
  type TaskDefinition,
  type WorkflowDefinition,
  type WorkflowSourceDefinition,
  type WorkflowDefinitionSnapshot,
} from './domain';
import {
  getWorkflowUseDefinition,
  inferWorkflowCheckUse,
  inferWorkflowTaskUse,
  isWorkflowUseAllowed,
} from './uses-catalog';

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
    stage: Stage;
    id: string;
    title: string;
    source: string;
    uses: string;
    dependsOn: string[];
    resultContract?: string;
    selfRepair?: boolean;
    useDescription?: string;
  }
  | {
    kind: 'check';
    stage: Stage;
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

export function resolveWorkflowDefinition(cwd: string = process.cwd()): ResolvedWorkflowDefinition {
  const overridePath = findWorkflowOverridePath(cwd);
  if (!overridePath) return resolveBuiltinDefault();

  const parsed = parseWorkflowOverride(overridePath);
  if (parsed.diagnostics.some(diagnostic => diagnostic.severity === 'error')) {
    return {
      snapshot: createWorkflowDefinitionSnapshot({
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
      snapshot: createWorkflowDefinitionSnapshot({
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
    snapshot: createWorkflowDefinitionSnapshot({
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
        message: 'stage id must be one of plan, build, check, integrate',
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
    definition: parseWorkflowDefinitionSource({ id, name, stages }, { taskSource: 'project', checkSource: 'project' }),
    diagnostics,
  };
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
    const task: TaskDefinition = {
      id: rawTask.id,
      title: typeof rawTask.title === 'string' ? rawTask.title : rawTask.id,
      source: 'project',
      uses: rawTask.uses,
      with: isRecord(rawTask.with) ? { ...rawTask.with } : undefined,
      emits: arrayValue(rawTask.emits).filter((value): value is string => typeof value === 'string'),
      dependsOn: arrayValue(rawTask.needs).filter((value): value is string => typeof value === 'string'),
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
    if (!isRecord(rawCheck) || typeof rawCheck.id !== 'string' || typeof rawCheck.uses !== 'string') {
      diagnostics.push({ severity: 'error', path: checkPath, message: 'check requires id and uses' });
      continue;
    }
    if (!isWorkflowUseAllowed(rawCheck.uses, 'check')) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.uses`, message: `Use '${rawCheck.uses}' is not allowed as a check` });
      continue;
    }
    if (rawCheck.uses === 'mohist/shell' && (!isRecord(rawCheck.with) || typeof rawCheck.with.command !== 'string')) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.with.command`, message: `Shell check '${rawCheck.id}' requires with.command` });
    }
    if (rawCheck.uses === 'mohist/artifact-exists' && (!isRecord(rawCheck.with) || typeof rawCheck.with.path !== 'string')) {
      diagnostics.push({ severity: 'error', path: `${checkPath}.with.path`, message: `Artifact check '${rawCheck.id}' requires with.path` });
    }
    const check: CheckDefinition = {
      name: rawCheck.id,
      title: typeof rawCheck.title === 'string' ? rawCheck.title : rawCheck.id,
      source: 'project',
      uses: rawCheck.uses,
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
  const task: TaskDefinition = {
    id: rawTask.id,
    title: typeof rawTask.title === 'string' ? rawTask.title : rawTask.id,
    source: 'project',
    uses: typeof rawTask.uses === 'string' ? rawTask.uses : 'mohist/agent',
    with: isRecord(rawTask.with) ? { ...rawTask.with } : undefined,
    emits: arrayValue(rawTask.emits).filter((value): value is string => typeof value === 'string'),
  };
  return {
    retry: {
      limit: retry.limit,
      task,
      inputFrom: compileReactionInputs(retry.inputFrom),
    },
  };
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
    if (rawPolicy.reset !== 'checks-and-approval' && rawPolicy.reset !== 'checks' && rawPolicy.reset !== 'approval') {
      diagnostics.push({ severity: 'error', path: `${stagePath}.on.${eventName}.reset`, message: 'reset must be checks-and-approval, checks, or approval' });
      continue;
    }
    const tasks = arrayValue(rawPolicy.tasks).filter((value): value is string => typeof value === 'string');
    let checks: 'all' | string[] | undefined;
    if (rawPolicy.checks === 'all') {
      checks = 'all';
    } else {
      const checkList = arrayValue(rawPolicy.checks).filter((value): value is string => typeof value === 'string');
      if (checkList.length > 0) checks = checkList;
    }
    on[eventName] = {
      reset: rawPolicy.reset,
      tasks: tasks.length > 0 ? tasks : undefined,
      checks,
      approval: typeof rawPolicy.approval === 'boolean' ? rawPolicy.approval : undefined,
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

function parseStageId(value: unknown): Stage | null {
  if (value === Stage.Plan || value === Stage.Build || value === Stage.Check || value === Stage.Integrate) {
    return value;
  }
  return null;
}

function resolveBuiltinDefault(): ResolvedWorkflowDefinition {
  return {
    snapshot: createWorkflowDefinitionSnapshot({
      definition: MOHIST_DEFAULT_WORKFLOW_DEFINITION,
      source: { type: 'builtin', id: MOHIST_DEFAULT_WORKFLOW_DEFINITION.id },
    }),
    sourceChain: ['mohist/default'],
    diagnostics: [],
  };
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
    applyRepairOverrides(stage, raw.repair, stagePath, diagnostics);
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

function applyRepairOverrides(
  stage: StageDefinition,
  rawRepair: unknown,
  stagePath: string,
  diagnostics: WorkflowDiagnostic[],
): void {
  if (!rawRepair) return;
  if (!isRecord(rawRepair)) {
    diagnostics.push({ severity: 'error', path: `${stagePath}.repair`, message: 'repair must be a mapping keyed by check name' });
    return;
  }
  for (const [checkName, raw] of Object.entries(rawRepair)) {
    if (!isRecord(raw) || typeof raw.maxAttempts !== 'number') {
      diagnostics.push({ severity: 'error', path: `${stagePath}.repair.${checkName}.maxAttempts`, message: 'repair maxAttempts must be a number' });
      continue;
    }
    let changed = false;
    const check = stage.checks.find(candidate => candidate.name === checkName);
    if (check?.onFailure?.retry) {
      check.onFailure.retry.limit = raw.maxAttempts;
      changed = true;
    }
    if (!changed) {
      diagnostics.push({ severity: 'error', path: `${stagePath}.repair.${checkName}`, message: `Unknown repair policy for check '${checkName}'` });
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
    stage.checks.push({
      name: raw.id,
      title: typeof raw.title === 'string' ? raw.title : raw.id,
      source: 'project',
      uses: raw.uses,
      with: isRecord(raw.with) ? { ...raw.with } : undefined,
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
  check.source = 'project';
  if (typeof raw.uses === 'string') check.uses = raw.uses;
  if (isRecord(raw.with)) check.with = { ...raw.with };
  const maxAttempts = isRecord(raw.repair) ? raw.repair.maxAttempts : raw.maxAttempts;
  if (maxAttempts !== undefined) {
    if (typeof maxAttempts !== 'number') {
      diagnostics.push({ severity: 'error', path: `${checkPath}.maxAttempts`, message: 'repair maxAttempts must be a number' });
    } else {
      if (check.onFailure?.retry) {
        check.onFailure.retry.limit = maxAttempts;
      }
    }
  }
}

export function validateWorkflowDefinition(resolved: ResolvedWorkflowDefinition = resolveWorkflowDefinition()): WorkflowDiagnostic[] {
  const diagnostics: WorkflowDiagnostic[] = [...resolved.diagnostics];
  const seenStages = new Set<Stage>();
  const isFullCustomWorkflow = resolved.sourceChain[0] !== 'mohist/default';

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
    for (const repair of stage.repairPolicies ?? []) {
      if (!checkNames.has(repair.checkName)) {
        diagnostics.push({ severity: 'error', path: `${stagePath}.repairPolicies`, message: `Repair policy references unknown check '${repair.checkName}'` });
      }
    }

    if (isFullCustomWorkflow && stage.requiresApproval && isProjectDefinedStage(stage) && hasAnyApprovalEvidence(stage) && !hasApprovalEvidenceShape(stage)) {
      diagnostics.push({
        severity: 'error',
        path: stagePath,
        message: 'Custom approval stage must declare complete approval evidence checks for verdict, verification, and candidate roles',
        suggestion: 'Set check.with.approvalEvidence.role to verdict, verification, and candidate; verdict/candidate roles also need snapshotField.',
      });
    }
  }

  return diagnostics;
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
  const policy = stage.taskExecutionPolicies?.find(candidate => candidate.taskId === task.id)
    ?? stage.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
  const uses = task.uses ?? inferWorkflowTaskUse(task.id, policy?.kind);
  const useDefinition = getWorkflowUseDefinition(uses);
  return {
    kind: 'task',
    stage: stage.stage,
    id: task.id,
    title: task.title,
    source: task.source ?? 'builtin',
    uses,
    dependsOn: task.dependsOn ?? [],
    resultContract: task.resultContract?.kind,
    selfRepair: task.selfRepairPolicy?.enabled,
    useDescription: useDefinition?.description,
  };
}

function explainCheck(stage: CompiledStageDefinition, check: CheckDefinition): ExplainedWorkflowItem {
  const phase = stage.checkPolicies?.find(candidate => candidate.checkName === check.name)?.phase ?? 'post-task';
  const reaction = stage.repairPolicies?.find(candidate => candidate.checkName === check.name)
    ?? stage.checkFailurePolicies?.find(candidate => candidate.checkName === check.name);
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
  const policy = stage.taskExecutionPolicies?.find(candidate => candidate.taskId === taskId)
    ?? stage.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
  return inferWorkflowTaskUse(taskId, policy?.kind);
}

function hasApprovalEvidenceShape(stage: CompiledStageDefinition): boolean {
  const roles = new Set<string>();
  for (const check of stage.checks) {
    const evidence = check.with?.approvalEvidence;
    if (!evidence || typeof evidence !== 'object' || Array.isArray(evidence)) continue;
    const role = (evidence as Record<string, unknown>).role;
    if (typeof role !== 'string') continue;
    if ((role === 'verdict' || role === 'candidate') && typeof (evidence as Record<string, unknown>).snapshotField !== 'string') continue;
    roles.add(role);
  }
  return roles.has('verdict') && roles.has('verification') && roles.has('candidate');
}

function hasAnyApprovalEvidence(stage: CompiledStageDefinition): boolean {
  return stage.checks.some(check => {
    const evidence = check.with?.approvalEvidence;
    return Boolean(evidence && typeof evidence === 'object' && !Array.isArray(evidence));
  });
}

function isProjectDefinedStage(stage: CompiledStageDefinition): boolean {
  return stage.tasks.some(task => task.source === 'project') || stage.checks.some(check => check.source === 'project');
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
