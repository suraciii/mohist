import type { StageDefinition } from './workflow-definition';

export type WorkflowDefinitionSnapshot = {
  workflowDefinitionId: string;
  stages: StageDefinition[];
};
