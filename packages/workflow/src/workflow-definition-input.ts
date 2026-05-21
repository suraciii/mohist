import YAML from 'yaml';
import {
  createResolvedWorkflowDefinition,
  type ResolvedWorkflowDefinition,
} from './model';
import { parseWorkflowDefinitionSource, type WorkflowSourceDefinition } from './definition/workflow-definition-source';
import type { WorkflowDefinitionInput } from './workflow-types';

export function resolvedWorkflowDefinitionFromInput(input: WorkflowDefinitionInput, capturedAt?: string): ResolvedWorkflowDefinition {
  if (isResolvedWorkflowDefinition(input)) return input;
  if (isYamlWorkflowInput(input)) {
    const parsed = YAML.parse(input.yaml);
    return createResolvedWorkflowDefinition({
      definition: parseWorkflowDefinitionSource(normalizeWorkflowSource(parsed)),
      source: input.source,
      capturedAt: input.capturedAt ?? capturedAt,
    });
  }
  if (isWorkflowSourceDefinition(input)) {
    return createResolvedWorkflowDefinition({
      definition: parseWorkflowDefinitionSource(input),
      capturedAt,
    });
  }
  return createResolvedWorkflowDefinition({ definition: input, capturedAt });
}

function isResolvedWorkflowDefinition(value: unknown): value is ResolvedWorkflowDefinition {
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
