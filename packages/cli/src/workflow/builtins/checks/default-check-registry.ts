import type { WorkflowDefinitionSnapshot } from '@mohist/workflow/internal/model';
import { createWorkflowTemplateContextFromValues, renderWorkflowTemplate } from '../../template';
import type { Check, CheckContext } from '@mohist/workflow/checks';
import { ArtifactExistsCheck } from './artifact-exists-check';
import { ArtifactMarkerCheck } from './artifact-marker-check';
import { createCheckRegistry, type CheckProvider, type CheckProviderInput, type CheckRegistry } from '@mohist/workflow/checks/check-registry';
import { HealthGateCheck } from './health-gate-check';
import { MergeReadyCheck } from './merge-ready-check';
import { ShellCommandCheck } from './shell-command-check';
import { UserApprovalCheck } from './user-approval-check';
import { registerMohistDefaultMarkerFormats } from '../workflows/mohist-default';

export function createDefaultCheckRegistry(input: {
  worktreePath: string;
  workflowDefinitionSnapshot?: WorkflowDefinitionSnapshot;
}): CheckRegistry {
  registerMohistDefaultMarkerFormats();
  const providers: CheckProvider[] = [
    {
      id: 'mohist/health-gate',
      build: ({ stage, check, worktreePath }) => new HealthGateCheck({
        worktreePath: worktreePath ?? input.worktreePath,
        stage,
        name: check.name,
        policy: {
          enabled: typeof check.with?.enabled === 'boolean' ? check.with.enabled : true,
          command: typeof check.with?.command === 'string' ? check.with.command : '',
          timeout: typeof check.with?.timeout === 'number' ? check.with.timeout : 5 * 60 * 1000,
          autoFix: typeof check.with?.autoFix === 'boolean' ? check.with.autoFix : false,
          maxFixAttempts: typeof check.with?.maxFixAttempts === 'number' ? check.with.maxFixAttempts : 0,
        },
      }),
    },
    {
      id: 'mohist/shell',
      build: ({ check }) => {
        if (typeof check.with?.command !== 'string') return null;
        return new ShellCommandCheck(check.name, {
          command: check.with.command,
          timeout: typeof check.with.timeout === 'number' ? check.with.timeout : undefined,
          cwd: typeof check.with.cwd === 'string' ? check.with.cwd : undefined,
        });
      },
    },
    {
      id: 'mohist/artifact-exists',
      build: providerInput => {
        if (typeof providerInput.check.with?.path !== 'string') return null;
        return new ArtifactExistsCheck(providerInput.check.name, renderCheckPath(providerInput));
      },
    },
    {
      id: 'mohist/marker',
      build: buildArtifactMarkerCheck,
    },
    {
      id: 'mohist/verdict',
      build: buildArtifactMarkerCheck,
    },
    {
      id: 'mohist/merge-ready',
      build: ({ check }) => namedCheck(check.name, new MergeReadyCheck()),
    },
    {
      id: 'mohist/approval',
      build: ({ check }) => namedCheck(check.name, new UserApprovalCheck()),
    },
  ];

  return createCheckRegistry({ providers });
}

function buildArtifactMarkerCheck(input: CheckProviderInput): Check | null {
  if (typeof input.check.with?.path !== 'string' || typeof input.check.with?.expect !== 'string') return null;
  const markers = Array.isArray(input.check.with.markers)
    ? input.check.with.markers.filter((marker): marker is string => typeof marker === 'string')
    : undefined;
  return new ArtifactMarkerCheck(
    input.check.name,
    renderCheckPath(input),
    input.check.with.expect,
    {
      format: typeof input.check.with.format === 'string' ? input.check.with.format : undefined,
      markers: markers && markers.length > 0 ? markers : [input.check.with.expect],
      verdicts: isStringRecord(input.check.with.verdicts) ? input.check.with.verdicts : undefined,
    },
  );
}

function isStringRecord(value: unknown): value is Record<string, 'PASS' | 'FAIL'> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  return Object.values(value).every(item => item === 'PASS' || item === 'FAIL');
}

function renderCheckPath(input: CheckProviderInput): string {
  return renderWorkflowTemplate(input.check.with!.path as string, createWorkflowTemplateContextFromValues({
    issueNumber: input.ctx.issue.number,
    issueTitle: input.ctx.issue.title,
    changeDir: input.ctx.changeDir,
    worktreePath: input.worktreePath ?? input.ctx.acpOptions.cwd,
    artifacts: input.workflowDefinitionSnapshot?.resolvedDefinition.artifacts,
  }));
}

function namedCheck(name: string, check: Check): Check {
  return {
    name,
    run: async (ctx: CheckContext) => {
      const result = await check.run(ctx);
      return { ...result, name };
    },
  };
}
