import { Stage } from '../../types';
import type {
  CheckDefinition,
  StageDefinition,
  StageEventPolicy,
  TaskDefinition,
  WorkflowDefinition,
  WorkflowTasksFromSource,
} from './types';

export type WorkflowTaskSourceDefinition = Omit<TaskDefinition, 'source'> & {
  source?: TaskDefinition['source'];
};

export type WorkflowCheckSourceDefinition = Omit<CheckDefinition, 'source' | 'name'> & {
  id?: string;
  name?: string;
  source?: CheckDefinition['source'];
};

export interface WorkflowStageSourceDefinition {
  id?: Stage;
  stage?: Stage;
  tasks?: WorkflowTaskSourceDefinition[];
  tasksFrom?: WorkflowTasksFromSource;
  checks?: WorkflowCheckSourceDefinition[];
  on?: Record<string, StageEventPolicy>;
  approval?: boolean;
}

export interface WorkflowSourceDefinition {
  id: string;
  name?: string;
  artifacts?: Record<string, string>;
  defaults?: Record<string, unknown>;
  stages: WorkflowStageSourceDefinition[];
}

export interface ParseWorkflowDefinitionOptions {
  taskSource?: TaskDefinition['source'];
  checkSource?: CheckDefinition['source'];
}

export function parseWorkflowDefinitionSource(
  source: WorkflowSourceDefinition,
  options: ParseWorkflowDefinitionOptions = {},
): WorkflowDefinition {
  return {
    id: source.id,
    name: source.name,
    artifacts: source.artifacts ? { ...source.artifacts } : undefined,
    defaults: source.defaults ? { ...source.defaults } : undefined,
    stages: source.stages.map(stage => parseStageSource(stage, options)),
  };
}

export function workflowDefinitionSourceToYaml(source: WorkflowSourceDefinition): string {
  return `workflow:\n${yamlValue(source, 1)}`;
}

function parseStageSource(
  source: WorkflowStageSourceDefinition,
  options: ParseWorkflowDefinitionOptions,
): StageDefinition {
  const stage = source.stage ?? source.id;
  if (!stage) {
    throw new Error(`Workflow stage requires id`);
  }

  const tasks = (source.tasks ?? []).map(task => ({
    ...task,
    source: task.source ?? options.taskSource,
    with: task.with ? { ...task.with } : undefined,
    emits: task.emits ? [...task.emits] : undefined,
    dependsOn: task.dependsOn ? [...task.dependsOn] : undefined,
    resultContract: task.resultContract ? { ...task.resultContract } : undefined,
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
      source: check.source ?? options.checkSource,
      with: check.with ? { ...check.with } : undefined,
      approvalEvidence: check.approvalEvidence ? { ...check.approvalEvidence } : undefined,
    };
  });

  return {
    stage,
    tasks,
    tasksFrom: source.tasksFrom,
    checks,
    on: source.on ? Object.fromEntries(Object.entries(source.on).map(([event, policy]) => [event, { ...policy }])) : undefined,
    requiresApproval: source.approval || undefined,
    approvalCheckName: source.approval ? 'user-approval' : undefined,
  };
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
