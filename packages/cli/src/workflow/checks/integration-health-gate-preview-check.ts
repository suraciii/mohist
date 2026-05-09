import type { Check, CheckContext, CheckResult } from './index';
import type { HealthGatePolicy } from '../workflow-loader';
import { loadHealthGatePolicies, loadWorkflow } from '../workflow-loader';

export class IntegrationHealthGatePreviewCheck implements Check {
  public readonly name = 'integration-health-gate-preview';

  async run(ctx: CheckContext): Promise<CheckResult> {
    try {
      const worktreePath = ctx.acpOptions?.cwd ?? ctx.changeDir;
      const wf = loadWorkflow(worktreePath);
      if (typeof wf === 'string') {
        return {
          name: this.name,
          status: 'error',
          message: `Failed to load workflow: ${wf}`,
        };
      }
      const policies = loadHealthGatePolicies(wf);
      const postMergePolicy: HealthGatePolicy = policies.postMerge;

      return {
        name: this.name,
        status: 'pass',
        message: 'Integration health gate policy metadata retrieved',
        output: {
          kind: 'integration-health-gate-preview',
          policyName: 'postMerge',
          command: postMergePolicy.command,
          timeout: postMergePolicy.timeout,
          enabled: postMergePolicy.enabled,
          autoFix: postMergePolicy.autoFix,
          maxFixAttempts: postMergePolicy.maxFixAttempts,
        },
      };
    } catch (err) {
      return {
        name: this.name,
        status: 'error',
        message: `Integration health gate preview error: ${err instanceof Error ? err.message : String(err)}`,
      };
    }
  }
}