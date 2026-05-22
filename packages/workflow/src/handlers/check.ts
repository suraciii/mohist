import type { CheckResult } from '../domain';

export interface WorkflowCheckInput {
  name: string;
  title: string;
  with?: Record<string, unknown>;
}

export interface CheckHandler {
  run(input: WorkflowCheckInput): Promise<CheckResult>;
}
