import type { ConfigInfo } from './config-schema';

export type IssueModelOverride = {
  model?: string | null;
  stageModels?: Record<string, string> | null;
};

export const EXECUTABLE_MODEL_STAGES = [
  'backlog',
  'plan',
  'build',
  'check',
  'integrate',
  'done',
] as const;

export type ExecutableModelStage = (typeof EXECUTABLE_MODEL_STAGES)[number];

export function isValidModelId(value: string): boolean {
  if (!value || typeof value !== 'string') return false;
  const idx = value.indexOf('/');
  return idx > 0 && idx < value.length - 1;
}

export function resolveStageModel(
  stage: string,
  config: ConfigInfo,
  issueOverride?: IssueModelOverride,
): string | undefined {
  if (issueOverride?.stageModels && stage in issueOverride.stageModels) {
    return issueOverride.stageModels[stage];
  }
  if (issueOverride?.model) {
    return issueOverride.model;
  }
  const stageModels = config.opencode?.stageModels;
  if (stageModels && stage in stageModels) {
    return stageModels[stage];
  }
  return config.opencode?.model;
}
