import { describe, expect, it } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Stage } from '../src/types';
import { createWorkflowDefinitionSnapshot, type WorkflowDefinition } from '../src/workflow/domain';
import { explainWorkflowItem, resolveWorkflowDefinition, validateWorkflowDefinition } from '../src/workflow/workflow-inspector';

describe('workflow inspector', () => {
  it('resolves the builtin default workflow used by runtime', () => {
    const resolved = resolveWorkflowDefinition();

    expect(resolved.sourceChain).toEqual(['mohist/default']);
    expect(resolved.snapshot.workflowId).toBe('mohist/default');
    expect(resolved.snapshot.compiledStageDefinitions.map(stage => stage.stage)).toEqual([
      Stage.Plan,
      Stage.Build,
      Stage.Check,
      Stage.Integrate,
    ]);
  });

  it('validates missing task dependencies with actionable diagnostics', () => {
    const definition: WorkflowDefinition = {
      id: 'invalid/dependency',
      stages: [
        {
          stage: Stage.Build,
          tasks: [{ id: 'T-002', title: 'Broken task', dependsOn: ['T-001'] }],
          checks: [],
        },
      ],
    };
    const snapshot = createWorkflowDefinitionSnapshot({ definition });
    const diagnostics = validateWorkflowDefinition({
      snapshot,
      sourceChain: ['invalid/dependency'],
      diagnostics: [],
    });

    expect(diagnostics).toEqual([
      expect.objectContaining({
        severity: 'error',
        path: 'stages[0].tasks[0].dependsOn',
        message: "Task 'T-002' depends on unknown task 'T-001'",
      }),
    ]);
  });

  it('explains builtin task and check source information', () => {
    expect(explainWorkflowItem('ai-review')).toMatchObject({
      kind: 'task',
      stage: Stage.Check,
      uses: 'agent-session',
      source: 'builtin',
      selfRepair: true,
    });
    expect(explainWorkflowItem('merge-ready')).toMatchObject({
      kind: 'check',
      stage: Stage.Check,
      uses: 'mohist/merge-ready',
      source: 'builtin',
      blocking: true,
    });
  });

  it('resolves extends mohist/default project overrides for inspection', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-override-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
extends: mohist/default
checks:
  health:build:
    uses: mohist/shell
    with:
      command: pnpm build
    repair:
      maxAttempts: 4
stages:
  plan:
    approval: false
  check:
    disable:
      checks:
        - merge-ready
    repair:
      review-passed:
        maxAttempts: 3
    checks:
      - id: lint
        title: Lint
        uses: mohist/shell
        with:
          command: pnpm lint
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      const plan = resolved.snapshot.compiledStageDefinitions.find(stage => stage.stage === Stage.Plan)!;
      const build = resolved.snapshot.compiledStageDefinitions.find(stage => stage.stage === Stage.Build)!;
      const check = resolved.snapshot.compiledStageDefinitions.find(stage => stage.stage === Stage.Check)!;

      expect(resolved.sourceChain[0]).toBe('mohist/default');
      expect(resolved.sourceChain[1]).toContain('.mohist/workflow.yaml');
      expect(plan.requiresApproval).toBe(false);
      expect(build.checks.find(candidate => candidate.name === 'health:build')).toMatchObject({
        source: 'project',
        uses: 'mohist/shell',
        with: { command: 'pnpm build' },
      });
      expect(build.repairPolicies?.find(policy => policy.checkName === 'health:build')?.maxAttempts).toBe(4);
      expect(check.checks.map(candidate => candidate.name)).toContain('lint');
      expect(check.checks.map(candidate => candidate.name)).not.toContain('merge-ready');
      expect(check.repairPolicies?.find(policy => policy.checkName === 'review-passed')?.maxAttempts).toBe(3);
      expect(explainWorkflowItem('lint', resolved)).toMatchObject({
        kind: 'check',
        source: 'project',
        uses: 'mohist/shell',
        inputs: { command: 'pnpm lint' },
      });
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('reports invalid project override paths before runtime use', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-invalid-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
extends: other/workflow
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([
        expect.objectContaining({
          severity: 'error',
          message: 'Only extends: mohist/default is supported',
        }),
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
