import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';
import { Stage } from '../types';
import {
  MOHIST_DEFAULT_WORKFLOW_DEFINITION,
  cloneWorkflowDefinition,
  createWorkflowDefinitionSnapshot,
  type CheckDefinition,
  type CheckFailurePolicy,
  type StageDefinition,
  type TaskDefinition,
  type WorkflowDefinition,
  type WorkflowDefinitionSnapshot,
} from './domain';
import { getWorkflowUseDefinition, isWorkflowUseAllowed } from './uses-catalog';

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
    if (stage.checks.length !== checkCount) {
      stage.checkPolicies = stage.checkPolicies?.filter(policy => policy.checkName !== value);
      stage.repairPolicies = stage.repairPolicies?.filter(policy => policy.checkName !== value);
      stage.checkFailurePolicies = stage.checkFailurePolicies?.filter(policy => policy.checkName !== value);
    }
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
    for (const policy of [...(stage.repairPolicies ?? []), ...(stage.checkFailurePolicies ?? [])]) {
      if (policy.checkName === checkName) {
        policy.maxAttempts = raw.maxAttempts;
        changed = true;
      }
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
    stage.checkPolicies = [
      ...(stage.checkPolicies ?? []),
      { checkName: raw.id, phase: 'post-task' },
    ];
  }
}

function applyCheckOverride(
  stage: StageDefinition,
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
      for (const policy of [...(stage.repairPolicies ?? []), ...(stage.checkFailurePolicies ?? [])]) {
        if (policy.checkName === check.name) policy.maxAttempts = maxAttempts;
      }
    }
  }
}

export function validateWorkflowDefinition(resolved: ResolvedWorkflowDefinition = resolveWorkflowDefinition()): WorkflowDiagnostic[] {
  const diagnostics: WorkflowDiagnostic[] = [...resolved.diagnostics];
  const seenStages = new Set<Stage>();

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
      const uses = check.uses ?? inferCheckUses(check.name);
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

function explainTask(stage: StageDefinition, task: TaskDefinition): ExplainedWorkflowItem {
  const policy = stage.taskExecutionPolicies?.find(candidate => candidate.taskId === task.id)
    ?? stage.taskExecutionPolicies?.find(candidate => candidate.taskId === '*');
  const uses = inferTaskUses(task.id, policy?.kind);
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

function explainCheck(stage: StageDefinition, check: CheckDefinition): ExplainedWorkflowItem {
  const phase = stage.checkPolicies?.find(candidate => candidate.checkName === check.name)?.phase ?? 'post-task';
  const reaction = stage.repairPolicies?.find(candidate => candidate.checkName === check.name)
    ?? stage.checkFailurePolicies?.find(candidate => candidate.checkName === check.name);
  const uses = check.uses ?? inferCheckUses(check.name);
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

function inferCheckUses(checkName: string): string {
  if (checkName.startsWith('health:')) return 'mohist/health-gate';
  if (checkName === 'review-passed' || checkName === 'self-review-passed') return 'mohist/verdict';
  if (checkName === 'merge-ready') return 'mohist/merge-ready';
  return 'mohist/artifact-exists';
}

function inferTaskUses(taskId: string, executionKind?: string): string {
  if (taskId === 'integrate:spec-sync') return 'mohist/openspec-sync';
  if (taskId === 'integrate:archive-change') return 'mohist/archive-change';
  if (taskId === 'integrate:merge') return 'mohist/merge';
  if (taskId === 'rebase-branch') return 'mohist/rebase';
  if (executionKind === 'ralph-task') return 'mohist/ralph-tasks';
  if (executionKind === 'service-call') return 'mohist/agent';
  return 'mohist/agent';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value));
}

function arrayValue(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}
