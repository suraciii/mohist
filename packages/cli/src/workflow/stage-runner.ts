import type { StageContext, StageRunResult } from './stage-context';
import type { WorkflowStageId } from '@mohist/workflow/internal/model';

export interface StageRunner {
  canHandle(stage: WorkflowStageId): boolean;
  materializeWork?(ctx: StageContext): Promise<boolean> | boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}
