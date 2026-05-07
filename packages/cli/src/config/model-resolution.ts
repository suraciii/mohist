import type { ConfigInfo } from './config-schema';

/**
 * Resolve the effective model for a given pipeline stage.
 *
 * Priority chain (highest to lowest):
 * 1. config.opencode.stageModels[stage] — stage-specific override
 * 2. config.opencode.model — global coder model
 * 3. undefined — falls back to opencode default
 *
 * Stage matching is case-sensitive.
 */
export function resolveStageModel(
  stage: string,
  config: ConfigInfo,
): string | undefined {
  const stageModels = config.opencode?.stageModels;
  if (stageModels && stage in stageModels) {
    return stageModels[stage];
  }
  return config.opencode?.model;
}
