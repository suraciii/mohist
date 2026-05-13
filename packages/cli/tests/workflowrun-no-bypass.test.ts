import { describe, expect, it } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';

const repoRoot = path.resolve(__dirname, '..');
const srcRoot = path.join(repoRoot, 'src');

const guardedFiles = [
  'workflow/base-stage-runner.ts',
  'workflow/workflow-engine.ts',
  'api/issues.ts',
  'openspec/ralph-executor.ts',
  'services/agent-runner-service.ts',
];

const repositorySource = 'db/workflow-run-repo.ts';

const forbiddenWorkflowRunServiceCalls = [
  'setStagePassed',
  'setStageFailed',
  'setStageAwaitingApproval',
  'setStageStarted',
  'setRunStatus',
  'upsertTask',
  'upsertCheck',
  'setApproval',
];

const forbiddenWorkflowRunRepoCalls = [
  'setStagePassed',
  'setStageFailed',
  'setStageAwaitingApproval',
  'setStageStarted',
  'setRunStatus',
  'updateStageRunStatus',
  'updateWorkflowRunStatus',
  'setApproval',
  'upsertTask',
  'upsertCheck',
];

describe('WorkflowRun bypass guards', () => {
  it('does not expose lifecycle CRUD shortcuts on WorkflowRunService', () => {
    const serviceSource = fs.readFileSync(path.join(srcRoot, 'services/workflow-run-service.ts'), 'utf-8');

    for (const method of forbiddenWorkflowRunServiceCalls) {
      expect(serviceSource, `${method} must not be a public WorkflowRunService method`).not.toMatch(
        new RegExp(`\\n\\s*${method}\\s*\\(`),
      );
    }
  });

  it('prevents runners, API routes, recovery, and Ralph from calling bypass methods', () => {
    for (const relativePath of guardedFiles) {
      const source = fs.readFileSync(path.join(srcRoot, relativePath), 'utf-8');
      for (const method of forbiddenWorkflowRunServiceCalls) {
        expect(source, `${relativePath} must use aggregate commands instead of workflowRunService.${method}`).not.toContain(
          `workflowRunService.${method}`,
        );
      }
      for (const method of forbiddenWorkflowRunRepoCalls) {
        expect(source, `${relativePath} must not call WorkflowRun repository ${method} directly`).not.toMatch(
          new RegExp(`\\b(?:workflowRunRepo|workflowRunRepository|runRepo|repo)\\.${method}\\s*\\(`),
        );
      }
    }
  });

  it('does not keep bypass row-writer shortcuts on WorkflowRunRepo', () => {
    const source = fs.readFileSync(path.join(srcRoot, repositorySource), 'utf-8');

    for (const method of ['updateStageRunStatus', 'updateWorkflowRunStatus', 'setApproval', 'upsertTask', 'upsertCheck']) {
      expect(source, `${method} must not be exposed on WorkflowRunRepo`).not.toMatch(
        new RegExp(`\\n\\s*(?!private\\s)${method}\\s*\\(`),
      );
    }

    for (const method of ['upsertTaskInternal', 'upsertCheckInternal']) {
      expect(source, `${method} must remain private to aggregate persistence`).toMatch(
        new RegExp(`\\n\\s*private\\s+${method}\\s*\\(`),
      );
    }
  });
});
