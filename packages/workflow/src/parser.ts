import YAML from 'yaml';
import type {
  CheckDefinition,
  StageDefinition,
  StageEventPolicy,
  TaskDefinition,
  WorkflowDefinition,
  WorkflowStageId,
  WorkflowTasksFromSource,
} from './domain';
import { validateWorkflowDefinition } from './domain';

export type WorkflowInput = WorkflowDefinition | WorkflowSource | { yaml: string };

export type WorkflowTaskSource = Omit<TaskDefinition, never>;

export type WorkflowCheckSource = Omit<CheckDefinition, 'name'> & {
  id?: string;
  name?: string;
};

export interface WorkflowStageSource {
  id?: WorkflowStageId;
  stage?: WorkflowStageId;
  tasks?: WorkflowTaskSource[];
  tasksFrom?: WorkflowTasksFromSource;
  checks?: WorkflowCheckSource[];
  on?: Record<string, StageEventPolicy>;
  approval?: boolean;
}

export interface WorkflowSource {
  id: string;
  name?: string;
  artifacts?: Record<string, string>;
  defaults?: Record<string, unknown>;
  stages: WorkflowStageSource[];
}

export function parseWorkflow(input: WorkflowInput): WorkflowDefinition {
  const definition = isWorkflowDefinition(input)
    ? input
    : parseSource(input);
  validateWorkflowDefinition(definition);
  return definition;
}

export function parseYaml(yaml: string): WorkflowDefinition {
  return parseSource(normalizeWorkflowSource(YAML.parse(yaml)));
}

export function toYaml(source: WorkflowSource): string {
  return `workflow:\n${yamlValue(source, 1)}`;
}

function parseSource(input: WorkflowSource | { yaml: string }): WorkflowDefinition {
  if (isYamlWorkflowInput(input)) return parseYaml(input.yaml);
  return {
    id: input.id,
    name: input.name,
    artifacts: input.artifacts ? { ...input.artifacts } : undefined,
    defaults: input.defaults ? { ...input.defaults } : undefined,
    stages: input.stages.map(stage => parseStageSource(stage)),
  };
}

function parseStageSource(source: WorkflowStageSource): StageDefinition {
  const stage = source.stage ?? source.id;
  if (!stage) {
    throw new Error('Workflow stage requires id');
  }

  const tasks = (source.tasks ?? []).map(task => ({
    ...task,
    with: task.with ? { ...task.with } : undefined,
    onSuccess: task.onSuccess ? {
      emit: task.onSuccess.emit ? [...task.onSuccess.emit] : undefined,
    } : undefined,
    dependsOn: task.dependsOn ? [...task.dependsOn] : undefined,
  }));

  const checks = (source.checks ?? []).map(check => {
    const name = check.name ?? check.id;
    if (!name) {
      throw new Error(`Workflow check in stage ${stage} requires id`);
    }
    return {
      ...check,
      name,
      id: undefined,
      with: check.with ? { ...check.with } : undefined,
    };
  });

  return {
    stage,
    tasks,
    tasksFrom: cloneTasksFrom(source.tasksFrom),
    checks,
    on: source.on ? Object.fromEntries(Object.entries(source.on).map(([event, policy]) => [event, {
      reset: {
        tasks: policy.reset.tasks ? [...policy.reset.tasks] : undefined,
        checks: Array.isArray(policy.reset.checks) ? [...policy.reset.checks] : policy.reset.checks,
        approval: policy.reset.approval,
      },
    }])) : undefined,
    requiresApproval: source.approval || undefined,
  };
}

function cloneTasksFrom(tasksFrom: WorkflowTasksFromSource | undefined): WorkflowTasksFromSource | undefined {
  if (!tasksFrom || typeof tasksFrom === 'string') return tasksFrom;
  return {
    uses: tasksFrom.uses,
    with: tasksFrom.with ? { ...tasksFrom.with } : undefined,
  };
}

function normalizeWorkflowSource(value: unknown): WorkflowSource {
  const source = isPlainObject(value) && isPlainObject(value.workflow)
    ? value.workflow
    : value;
  if (!isWorkflowSource(source)) {
    throw new Error('Workflow YAML must define workflow id and stages');
  }
  return source;
}

function isWorkflowDefinition(value: unknown): value is WorkflowDefinition {
  return Boolean(
    isPlainObject(value)
      && typeof value.id === 'string'
      && Array.isArray(value.stages)
      && value.stages.every(stage => isPlainObject(stage) && typeof stage.stage === 'string'),
  );
}

function isYamlWorkflowInput(value: unknown): value is { yaml: string } {
  return Boolean(value && typeof value === 'object' && 'yaml' in value && typeof value.yaml === 'string');
}

function isWorkflowSource(value: unknown): value is WorkflowSource {
  return Boolean(
    isPlainObject(value)
      && typeof value.id === 'string'
      && Array.isArray(value.stages),
  );
}

function yamlValue(value: unknown, indentLevel: number): string {
  const indent = '  '.repeat(indentLevel);
  if (Array.isArray(value)) {
    if (value.length === 0) return '[]\n';
    return value.map(item => {
      if (isPlainObject(item)) {
        const entries = Object.entries(item).filter(([, itemValue]) => itemValue !== undefined);
        if (entries.length === 0) return `${indent}- {}\n`;
        const [firstKey, firstValue] = entries[0];
        let output = `${indent}- ${firstKey}:${yamlInlineOrNested(firstValue, indentLevel + 1)}`;
        for (const [key, itemValue] of entries.slice(1)) {
          output += `${indent}  ${key}:${yamlInlineOrNested(itemValue, indentLevel + 1)}`;
        }
        return output;
      }
      return `${indent}- ${yamlScalar(item)}\n`;
    }).join('');
  }
  if (isPlainObject(value)) {
    const entries = Object.entries(value).filter(([, itemValue]) => itemValue !== undefined);
    if (entries.length === 0) return '{}\n';
    return entries.map(([key, itemValue]) => `${indent}${key}:${yamlInlineOrNested(itemValue, indentLevel)}`).join('');
  }
  return `${yamlScalar(value)}\n`;
}

function yamlInlineOrNested(value: unknown, indentLevel: number): string {
  if (isMultilineString(value)) return ` |-\n${indentMultiline(value, indentLevel + 1)}`;
  if (Array.isArray(value) || isPlainObject(value)) return `\n${yamlValue(value, indentLevel + 1)}`;
  return ` ${yamlScalar(value)}\n`;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value));
}

function isMultilineString(value: unknown): value is string {
  return typeof value === 'string' && value.includes('\n');
}

function indentMultiline(value: string, indentLevel: number): string {
  const indent = '  '.repeat(indentLevel);
  return value.split('\n').map(line => `${indent}${line}`).join('\n') + '\n';
}

function yamlScalar(value: unknown): string {
  if (value === null) return 'null';
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  if (typeof value !== 'string') return JSON.stringify(value);
  if (value.length === 0) return "''";
  if (isBareYamlString(value)) return value;
  return JSON.stringify(value);
}

function isBareYamlString(value: string): boolean {
  return /^[A-Za-z0-9_./:{}-]+$/.test(value)
    && value !== 'true'
    && value !== 'false'
    && value !== 'null'
    && !/^-?\d+(\.\d+)?$/.test(value);
}
