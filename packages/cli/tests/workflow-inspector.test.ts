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
      uses: 'mohist/agent',
      source: 'builtin',
      selfRepair: true,
      useDescription: expect.stringContaining('ACP session'),
    });
    expect(explainWorkflowItem('merge-ready')).toMatchObject({
      kind: 'check',
      stage: Stage.Check,
      uses: 'mohist/merge-ready',
      source: 'builtin',
      blocking: true,
    });
  });

  it('keeps builtin health gates in the semantic workflow definition', () => {
    const resolved = resolveWorkflowDefinition();
    const build = resolved.snapshot.compiledStageDefinitions.find(stage => stage.stage === Stage.Build)!;
    const health = build.checks.find(check => check.name === 'health:build');

    expect(health).toMatchObject({
      uses: 'mohist/health-gate',
      with: {
        command: 'npm ci && npm run build',
        timeout: 300000,
      },
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

  it('rejects mutation-only catalog uses when declared as checks', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-invalid-use-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
extends: mohist/default
stages:
  check:
    checks:
      - id: unsafe-merge
        uses: mohist/merge
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([
        expect.objectContaining({
          severity: 'error',
          message: "Use 'mohist/merge' is not allowed as a check",
        }),
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('resolves full custom workflow definitions into project sourced stages', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-custom-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/custom
  name: Project Custom
  stages:
    - id: plan
      tasks:
        - id: design
          title: Design
          uses: mohist/agent
          with:
            prompt: Write a compact design.
            outputs:
              - design.md
      checks:
        - id: design-file
          title: Design file
          uses: mohist/artifact-exists
          with:
            path: design.md
      approval: true
    - id: build
      tasks:
        - id: implement
          uses: mohist/agent
          with:
            prompt: Implement the design.
            session: build-agent
      checks:
        - id: build-clean
          uses: mohist/shell
          with:
            command: npm run build
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      const diagnostics = validateWorkflowDefinition(resolved);
      const plan = resolved.snapshot.compiledStageDefinitions[0];
      const build = resolved.snapshot.compiledStageDefinitions[1];

      expect(diagnostics).toEqual([]);
      expect(resolved.sourceChain).toHaveLength(1);
      expect(resolved.snapshot.workflowId).toBe('project/custom');
      expect(resolved.snapshot.source).toMatchObject({ type: 'project' });
      expect(plan.stage).toBe(Stage.Plan);
      expect(plan.requiresApproval).toBe(true);
      expect(plan.tasks[0]).toMatchObject({
        id: 'design',
        source: 'project',
        uses: 'mohist/agent',
        with: { prompt: 'Write a compact design.', outputs: ['design.md'] },
      });
      expect(plan.checks[0]).toMatchObject({
        name: 'design-file',
        source: 'project',
        uses: 'mohist/artifact-exists',
        with: { path: 'design.md' },
      });
      expect(build.taskExecutionPolicies?.find(policy => policy.taskId === 'implement')).toMatchObject({
        kind: 'agent-session',
        agentSessionRef: 'build-agent',
      });
      expect(explainWorkflowItem('build-clean', resolved)).toMatchObject({
        kind: 'check',
        source: 'project',
        uses: 'mohist/shell',
        inputs: { command: 'npm run build' },
      });
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('accepts object prompt sources for custom agent tasks', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-prompt-source-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.mkdirSync(path.join(tempDir, '.mohist', 'prompts'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'prompts', 'handoff.md'), 'Write handoff.', 'utf-8');
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/prompt-source
  stages:
    - id: build
      tasks:
        - id: inline-report
          uses: mohist/agent
          with:
            prompt:
              inline: Write a short report.
        - id: file-report
          uses: mohist/agent
          with:
            prompt:
              file: .mohist/prompts/handoff.md
        - id: builtin-report
          uses: mohist/agent
          with:
            prompt:
              ref: mohist/check/ai-review
      checks: []
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      const diagnostics = validateWorkflowDefinition(resolved);
      const build = resolved.snapshot.compiledStageDefinitions[0];

      expect(diagnostics).toEqual([]);
      expect(build.tasks.map(task => task.id)).toEqual(['inline-report', 'file-report', 'builtin-report']);
      expect(build.tasks[0].with).toMatchObject({ prompt: { inline: 'Write a short report.' } });
      expect(build.tasks[1].with).toMatchObject({ prompt: { file: '.mohist/prompts/handoff.md' } });
      expect(build.tasks[2].with).toMatchObject({ prompt: { ref: 'mohist/check/ai-review' } });
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('compiles check-local onFailure retry into repair policies', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-on-failure-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/on-failure
  stages:
    - id: check
      on:
        code.changed:
          reset: checks-and-approval
          tasks: [ai-review]
          checks: all
          approval: true
      tasks:
        - id: ai-review
          uses: mohist/agent
          with:
            prompt:
              inline: Review the change.
      checks:
        - id: health:check
          uses: mohist/health-gate
          with:
            approvalEvidence:
              role: verification
        - id: review-passed
          uses: mohist/verdict
          with:
            approvalEvidence:
              role: verdict
              snapshotField: snapshotSha
          onFailure:
            retry:
              limit: 2
              task:
                id: fix-review-findings
                title: Fix review findings
                uses: mohist/agent
                emits: [code.changed]
                with:
                  prompt:
                    inline: |
                      Fix findings in {{ openspec.changeDir }}/review.md
        - id: merge-ready
          uses: mohist/merge-ready
          with:
            approvalEvidence:
              role: candidate
              snapshotField: candidateHeadSha
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      const diagnostics = validateWorkflowDefinition(resolved);
      const check = resolved.snapshot.compiledStageDefinitions[0];

      expect(diagnostics).toEqual([]);
      expect(check.checks.find(candidate => candidate.name === 'review-passed')?.onFailure?.retry?.limit).toBe(2);
      expect(check.repairPolicies?.find(policy => policy.checkName === 'review-passed')).toMatchObject({
        fixTaskId: 'fix-review-findings',
        fixTaskTitle: 'Fix review findings',
        maxAttempts: 2,
      });
      expect(check.taskExecutionPolicies?.find(policy => policy.taskId === 'fix-review-findings')).toMatchObject({
        kind: 'agent-session',
        workSourceKind: 'runtime',
      });
      expect(check.invalidationPolicy?.entries).toContainEqual(expect.objectContaining({
        triggerTaskId: 'fix-review-findings',
        invalidates: {
          tasks: ['ai-review'],
          checks: ['health:check', 'review-passed', 'merge-ready'],
          approval: true,
        },
      }));
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('rejects custom check stage shapes without approval evidence roles before runtime', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-custom-check-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  stages:
    - id: check
      tasks:
        - id: review
          uses: mohist/agent
          with:
            prompt: Review the change.
      checks:
        - id: review-file
          uses: mohist/artifact-exists
          with:
            path: review.md
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([
        expect.objectContaining({
          severity: 'error',
          message: 'Custom Check stage must declare approval evidence checks for verdict, verification, and candidate roles',
        }),
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('allows full custom Integrate stages without default local merge tasks', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-custom-integrate-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/report-integrate
  stages:
    - id: build
      tasks:
        - id: implement
          uses: mohist/agent
          with:
            prompt: Implement the issue.
      checks:
        - id: tests
          uses: mohist/shell
          with:
            command: npm test
    - id: integrate
      tasks:
        - id: handoff-report
          uses: mohist/agent
          with:
            prompt: Write handoff report.
      checks:
        - id: report-file
          uses: mohist/artifact-exists
          with:
            path: handoff.md
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('allows full custom workflows to declare builtin service task uses', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-custom-service-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/service-delivery
  stages:
    - id: integrate
      tasks:
        - id: sync-specs
          uses: mohist/openspec-sync
        - id: archive-spec-change
          uses: mohist/archive-change
        - id: land-locally
          uses: mohist/merge
      checks: []
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      const diagnostics = validateWorkflowDefinition(resolved);
      const integrate = resolved.snapshot.compiledStageDefinitions[0];

      expect(diagnostics).toEqual([]);
      expect(integrate.tasks.map(task => [task.id, task.uses])).toEqual([
        ['sync-specs', 'mohist/openspec-sync'],
        ['archive-spec-change', 'mohist/archive-change'],
        ['land-locally', 'mohist/merge'],
      ]);
      const serviceTaskIds = new Set(['sync-specs', 'archive-spec-change', 'land-locally']);
      expect(integrate.taskExecutionPolicies
        ?.filter(policy => serviceTaskIds.has(policy.taskId))
        .map(policy => [policy.taskId, policy.kind])).toEqual([
        ['sync-specs', 'service-call'],
        ['archive-spec-change', 'service-call'],
        ['land-locally', 'service-call'],
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('rejects catalog task uses that do not have an executable task handler yet', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-unsupported-task-use-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/unsupported-task-use
  stages:
    - id: build
      tasks:
        - id: script
          uses: mohist/shell
          with:
            command: npm test
      checks: []
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([
        expect.objectContaining({
          severity: 'error',
          message: "Use 'mohist/shell' is not supported for full custom task execution yet",
        }),
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
