import { describe, it, expect } from 'vitest';
import { resolveStageModel } from '../../src/config/model-resolution';
import type { ConfigInfo } from '../../src/config/config-schema';

describe('resolveStageModel', () => {
  it('returns stage-specific model when stageModels override exists', () => {
    const config: ConfigInfo = {
      opencode: { model: 'm1', stageModels: { plan: 'm2' } },
    };
    expect(resolveStageModel('plan', config)).toBe('m2');
  });

  it('falls back to global model when no stage-specific override', () => {
    const config: ConfigInfo = {
      opencode: { model: 'm1', stageModels: { plan: 'm2' } },
    };
    expect(resolveStageModel('build', config)).toBe('m1');
  });

  it('returns undefined when config is empty', () => {
    const config: ConfigInfo = {};
    expect(resolveStageModel('check', config)).toBeUndefined();
  });

  it('falls back to global model when stageModels is absent', () => {
    const config: ConfigInfo = {
      opencode: { model: 'm1' },
    };
    expect(resolveStageModel('plan', config)).toBe('m1');
  });

  it('is case-sensitive — mismatched casing falls back to global model', () => {
    const config: ConfigInfo = {
      opencode: { model: 'm1', stageModels: { plan: 'm2' } },
    };
    expect(resolveStageModel('Plan', config)).toBe('m1');
  });
});
