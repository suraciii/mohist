import type { Check, CheckContext, CheckResult } from './index';
import type { CheckDefinition, ResolvedWorkflowDefinition, WorkflowStageId } from '../model';

export interface CheckProviderInput {
  ctx: CheckContext;
  stage: WorkflowStageId;
  check: CheckDefinition;
  worktreePath?: string;
  definition?: ResolvedWorkflowDefinition;
}

export interface CheckProvider {
  id: string;
  build(input: CheckProviderInput): Promise<Check | null> | Check | null;
}

export interface CheckRegistry {
  getProvider(id: string): CheckProvider | undefined;
  register(id: string, provider: CheckProvider): void;
  build(input: CheckProviderInput): Promise<Check | null>;
  listProviders(): string[];
}

export interface CheckRegistryOptions {
  providers?: CheckProvider[];
}

export function createCheckRegistry(
  input: CheckRegistryOptions = {},
): CheckRegistry {
  const providers = new Map<string, CheckProvider>();
  for (const provider of input.providers ?? []) {
    providers.set(provider.id, provider);
  }

  return {
    getProvider(id) {
      return providers.get(id);
    },
    register(id, provider) {
      providers.set(id, { ...provider, id });
    },
    async build(providerInput) {
      if (!providerInput.check.uses) return null;
      const provider = providers.get(providerInput.check.uses);
      return provider ? provider.build(providerInput) : null;
    },
    listProviders() {
      return [...providers.keys()];
    },
  };
}

export async function resolveCheck(
  registry: CheckRegistry,
  ctx: CheckContext,
  input: Omit<CheckProviderInput, 'ctx'>,
): Promise<Check> {
  const check = await registry.build({ ...input, ctx });
  if (!check) {
    throw new Error(`Check "${input.check.name}" uses "${input.check.uses ?? '<none>'}" is not registered`);
  }
  return check;
}

export async function runCheck(
  registry: CheckRegistry,
  ctx: CheckContext,
  input: Omit<CheckProviderInput, 'ctx'>,
): Promise<CheckResult> {
  const check = await resolveCheck(registry, ctx, input);
  return check.run(ctx);
}
