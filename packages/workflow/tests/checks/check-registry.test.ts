import { describe, expect, it } from 'vitest';
import {
  createCheckRegistry,
  resolveCheck,
  runCheck,
  type Check,
  type CheckContext,
} from '../../src';

describe('check registry', () => {
  it('builds and runs checks through provider uses ids', async () => {
    const check: Check = {
      name: 'custom-check',
      title: 'Custom check',
      run: async () => ({
        name: 'custom-check',
        title: 'Custom check',
        status: 'pass',
      }),
    };
    const registry = createCheckRegistry({
      providers: [
        {
          id: 'custom/check',
          build: ({ check: definition }) => definition.name === 'custom-check' ? check : null,
        },
      ],
    });

    expect(registry.listProviders()).toEqual(['custom/check']);
    await expect(resolveCheck(registry, {} as CheckContext, {
      stage: 'verify',
      check: { name: 'custom-check', title: 'Custom check', uses: 'custom/check' },
    })).resolves.toBe(check);
    await expect(runCheck(registry, {} as CheckContext, {
      stage: 'verify',
      check: { name: 'custom-check', title: 'Custom check', uses: 'custom/check' },
    })).resolves.toMatchObject({ status: 'pass' });
  });

  it('fails clearly when a check use is not registered', async () => {
    const registry = createCheckRegistry();

    await expect(resolveCheck(registry, {} as CheckContext, {
      stage: 'verify',
      check: { name: 'missing', title: 'Missing', uses: 'custom/missing' },
    })).rejects.toThrow('uses "custom/missing" is not registered');
  });
});
