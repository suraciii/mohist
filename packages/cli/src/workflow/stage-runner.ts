import { Stage } from '../types';
import type { StageContext, StageRunResult } from './stage-context';

export interface StageRunner {
  canHandle(stage: Stage): boolean;
  materializeWork?(ctx: StageContext): Promise<boolean> | boolean;
  run(ctx: StageContext): Promise<StageRunResult>;
}
