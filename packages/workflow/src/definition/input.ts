import YAML from 'yaml';
import { validateWorkflowDefinition, type WorkflowDefinition } from '../domain';
import { parseWorkflowDefinitionSource, type WorkflowSourceDefinition } from './source';

export type WorkflowDefinitionInput =
  | WorkflowDefinition
  | WorkflowSourceDefinition
  | { yaml: string };

export function workflowDefinitionFromInput(input: WorkflowDefinitionInput): WorkflowDefinition {
  if (isWorkflowDefinition(input)) {
    validateWorkflowDefinition(input);
    return input;
  }
  if (isYamlWorkflowInput(input)) {
    const parsed = YAML.parse(input.yaml);
    const source = normalizeWorkflowSource(parsed);
    return parseWorkflowDefinitionSource(source);
  }
  return parseWorkflowDefinitionSource(input);
}

function isWorkflowDefinition(value: unknown): value is WorkflowDefinition {
  return Boolean(
    isPlainObject(value)
      && typeof value.id === 'string'
      && Array.isArray(value.stages),
  );
}

function isYamlWorkflowInput(value: WorkflowDefinitionInput): value is Extract<WorkflowDefinitionInput, { yaml: string }> {
  return Boolean(value && typeof value === 'object' && 'yaml' in value && typeof value.yaml === 'string');
}

function normalizeWorkflowSource(value: unknown): WorkflowSourceDefinition {
  const source = isPlainObject(value) && isPlainObject(value.workflow)
    ? value.workflow
    : value;
  if (!isWorkflowSourceDefinition(source)) {
    throw new Error('Workflow YAML must define workflow id and stages');
  }
  return source;
}

function isWorkflowSourceDefinition(value: unknown): value is WorkflowSourceDefinition {
  return Boolean(
    isPlainObject(value)
      && typeof value.id === 'string'
      && Array.isArray(value.stages),
  );
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return Boolean(value && typeof value === 'object' && !Array.isArray(value));
}
