import YAML from 'yaml';
import {
  createWorkflowDefinitionSnapshot,
  type WorkflowDefinitionSnapshot,
} from '../model';
import { parseWorkflowDefinitionSource, type WorkflowSourceDefinition } from '../definition/workflow-definition-source';
import type { WorkflowDefinitionInput } from './types';

export function workflowDefinitionSnapshotFromInput(input: WorkflowDefinitionInput, capturedAt?: string): WorkflowDefinitionSnapshot {
  if (isWorkflowDefinitionSnapshot(input)) return input;
  if (isYamlWorkflowInput(input)) {
    const parsed = YAML.parse(input.yaml);
    return createWorkflowDefinitionSnapshot({
      definition: parseWorkflowDefinitionSource(normalizeWorkflowSource(parsed)),
      source: input.source,
      capturedAt: input.capturedAt ?? capturedAt,
    });
  }
  if (isWorkflowSourceDefinition(input)) {
    return createWorkflowDefinitionSnapshot({
      definition: parseWorkflowDefinitionSource(input),
      capturedAt,
    });
  }
  return createWorkflowDefinitionSnapshot({ definition: input, capturedAt });
}

function isWorkflowDefinitionSnapshot(value: unknown): value is WorkflowDefinitionSnapshot {
  return isPlainObject(value)
    && 'workflowId' in value
    && 'resolvedDefinition' in value
    && 'compiledStageDefinitions' in value;
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
