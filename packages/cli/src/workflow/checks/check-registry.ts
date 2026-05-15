import type { Check, CheckContext, CheckResult } from './index';

export type CheckFactory = (ctx: CheckContext) => Promise<Check> | Check;

export interface CheckRegistry {
  get(name: string): CheckFactory | undefined;
  register(name: string, factory: CheckFactory): void;
  list(): string[];
}

export function createCheckRegistry(
  factories: Record<string, CheckFactory> = {},
): CheckRegistry {
  const map = new Map<string, CheckFactory>(Object.entries(factories));
  return {
    get(name) {
      return map.get(name);
    },
    register(name, factory) {
      map.set(name, factory);
    },
    list() {
      return [...map.keys()];
    },
  };
}

export async function resolveCheck(
  registry: CheckRegistry,
  ctx: CheckContext,
  checkName: string,
): Promise<Check> {
  const factory = registry.get(checkName);
  if (!factory) {
    throw new Error(`Check "${checkName}" is not registered`);
  }
  return factory(ctx);
}

export async function runCheck(
  registry: CheckRegistry,
  ctx: CheckContext,
  checkName: string,
): Promise<CheckResult> {
  const check = await resolveCheck(registry, ctx, checkName);
  return check.run(ctx);
}