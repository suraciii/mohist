import { describe, expect, it } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as yaml from 'yaml';
import { Stage } from '../src/types';
import { createWorkflowDefinitionSnapshot, type CheckFailurePolicy, type WorkflowDefinition } from '../src/workflow/model';
import { MOHIST_DEFAULT_WORKFLOW_DEFINITION } from '../src/workflow/definition/default-workflow';
import { explainWorkflowItem, getBuiltinDefaultWorkflowYaml, resolveWorkflowDefinition, validateWorkflowDefinition } from '../src/workflow/definition/workflow-inspector';

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

  it('exposes a complete default workflow YAML that round-trips to the builtin definition', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-default-workflow-yaml-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    const yamlText = getBuiltinDefaultWorkflowYaml();
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), yamlText, 'utf-8');

    try {
      const parsed = yaml.parse(yamlText);
      expect(parsed.workflow.id).toBe('mohist/default');
      expect(parsed.workflow.artifacts).toEqual({ openspecChange: '{{ openspec.changeDir }}' });
      expect(parsed.workflow.stages[0].tasks.find((task: any) => task.id === 'self-review').with.requiredMarkers).toEqual([
        {
          path: '{{ artifacts.openspecChange }}/self-review.md',
          markers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
          onMissing: { action: 'continue-session', maxAttempts: 1 },
        },
      ]);
      expect(parsed.workflow.stages[2].tasks.find((task: any) => task.id === 'ai-review')).not.toHaveProperty('selfRepairPolicy');
      expect(parsed.workflow.stages[2].checks.find((check: any) => check.id === 'review-passed')).toMatchObject({
        uses: 'mohist/marker',
        with: {
          path: '{{ artifacts.openspecChange }}/review.md',
          expect: '<promise>PASS</promise>',
        },
      });
      expect(parsed.workflow.stages[2].checks.find((check: any) => check.id === 'review-passed')).not.toHaveProperty('approvalEvidence');
      expect(parsed.workflow.stages[2].checks.find((check: any) => check.id === 'health:check').with).not.toHaveProperty('approvalEvidence');
      expect(parsed.workflow.stages[2].checks.find((check: any) => check.id === 'merge-ready').with).toBeUndefined();
      expect(parsed.workflow.stages[2].checks.find((check: any) => check.id === 'merge-ready')).not.toHaveProperty('approvalEvidence');
      expect(yamlText).not.toContain('mohist/plan/fix-review');
      expect(yamlText).not.toContain('mohist/build/fix-health');
      expect(yamlText).not.toContain('mohist/check/fix-health');
      expect(yamlText).not.toContain('mohist/integrate/fix-health');

      const resolved = resolveWorkflowDefinition(tempDir);
      expect(validateWorkflowDefinition(resolved)).toEqual([]);
      expect(toSemanticWorkflowDefinition(resolved.snapshot.resolvedDefinition)).toEqual(
        toSemanticWorkflowDefinition(MOHIST_DEFAULT_WORKFLOW_DEFINITION),
      );
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
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

  it('accepts arbitrary stage ids from complete workflow YAML', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-custom-stage-workflow-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/custom-stage
  stages:
    - id: triage
      tasks:
        - id: summarize
          title: Summarize issue
          uses: mohist/agent
          with:
            prompt:
              inline: "Summarize {{ issue.title }}"
      checks:
        - id: summary-exists
          title: Summary exists
          uses: mohist/artifact-exists
          with:
            path: summary.md
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      expect(validateWorkflowDefinition(resolved)).toEqual([]);
      expect(resolved.snapshot.compiledStageDefinitions.map(stage => stage.stage)).toEqual(['triage']);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('explains builtin task and check source information', () => {
    expect(explainWorkflowItem('ai-review')).toMatchObject({
      kind: 'task',
      stage: Stage.Check,
      uses: 'mohist/check/ai-review',
      source: 'builtin',
      useDescription: expect.stringContaining('ACP agent session'),
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
    onFailure:
      retry:
        limit: 4
        task:
          id: fix-build-health
          title: Fix build health
          uses: mohist/agent
          with:
            prompt:
              ref: mohist/build/fix-health
  review-passed:
    uses: mohist/verdict
    onFailure:
      retry:
        limit: 3
        task:
          id: fix-review-findings
          title: Fix review findings
          uses: mohist/agent
          with:
            prompt:
              inline: Fix review findings.
stages:
  plan:
    approval: false
  check:
    disable:
      checks:
        - merge-ready
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
      expect(build.checkFailurePolicies?.find(policy => policy.checkName === 'health:build')?.maxAttempts).toBe(4);
      expect(check.checks.map(candidate => candidate.name)).toContain('lint');
      expect(check.checks.map(candidate => candidate.name)).not.toContain('merge-ready');
      expect(check.checkFailurePolicies?.find(policy => policy.checkName === 'review-passed')?.maxAttempts).toBe(3);
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

  it('compiles check-local onFailure retry into check failure policies', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-on-failure-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/on-failure
  stages:
    - id: check
      on:
        code.changed:
          reset:
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
        - id: review-passed
          uses: mohist/verdict
          onFailure:
            retry:
              limit: 2
              task:
                id: fix-review-findings
                title: Fix review findings
                uses: mohist/agent
                with:
                  prompt:
                    inline: |
                      Fix findings in {{ openspec.changeDir }}/review.md
        - id: merge-ready
          uses: mohist/merge-ready
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      const diagnostics = validateWorkflowDefinition(resolved);
      const check = resolved.snapshot.compiledStageDefinitions[0];

      expect(diagnostics).toEqual([]);
      expect(check.checks.find(candidate => candidate.name === 'review-passed')?.onFailure?.retry?.limit).toBe(2);
      expect(check.checkFailurePolicies?.find(policy => policy.checkName === 'review-passed')).toMatchObject({
        fixTaskId: 'fix-review-findings',
        fixTaskTitle: 'Fix review findings',
        maxAttempts: 2,
      });
      expect(check.taskExecutionPolicies?.find(policy => policy.taskId === 'fix-review-findings')).toMatchObject({
        kind: 'agent-session',
        workSourceKind: 'runtime',
      });
      expect(check.invalidationPolicy?.entries).toContainEqual(expect.objectContaining({
        eventName: 'code.changed',
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

  it('preserves onFailure retry for checks added by default workflow overrides', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-stage-check-retry-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
extends: mohist/default
stages:
  check:
    checks:
      - id: custom-verdict
        title: Custom verdict
        uses: mohist/verdict
        onFailure:
          retry:
            limit: 1
            task:
              id: fix-custom-verdict
              title: Fix custom verdict
              uses: mohist/agent
              with:
                prompt:
                  inline: Fix the custom verdict failure.
`, 'utf-8');

    try {
      const resolved = resolveWorkflowDefinition(tempDir);
      const diagnostics = validateWorkflowDefinition(resolved);
      const check = resolved.snapshot.compiledStageDefinitions.find(stage => stage.stage === Stage.Check)!;

      expect(diagnostics).toEqual([]);
      expect(check.checks.find(candidate => candidate.name === 'custom-verdict')?.onFailure?.retry).toMatchObject({
        limit: 1,
        task: {
          id: 'fix-custom-verdict',
          uses: 'mohist/agent',
          with: { prompt: { inline: 'Fix the custom verdict failure.' } },
        },
      });
      expect(check.checkFailurePolicies?.find(policy => policy.checkName === 'custom-verdict')).toMatchObject({
        fixTaskId: 'fix-custom-verdict',
        maxAttempts: 1,
      });
      expect(check.taskExecutionPolicies?.find(policy => policy.taskId === 'fix-custom-verdict')).toMatchObject({
        kind: 'agent-session',
        workSourceKind: 'runtime',
      });
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('rejects legacy event reset shortcuts', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-legacy-reset-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/legacy-reset
  stages:
    - id: check
      on:
        code.changed:
          reset: checks-and-approval
      tasks: []
      checks: []
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([
        expect.objectContaining({
          severity: 'error',
          path: expect.stringContaining('workflow.stages[0].on.code.changed.reset'),
          message: 'reset must be a mapping with at least one target',
        }),
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('rejects task emits in custom workflow YAML', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-task-emits-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/task-emits
  stages:
    - id: check
      tasks:
        - id: fix
          uses: mohist/agent
          emits: [code.changed]
          with:
            prompt:
              inline: Fix the code.
      checks: []
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([
        expect.objectContaining({
          severity: 'error',
          path: expect.stringContaining('workflow.stages[0].tasks[0].emits'),
          message: expect.stringContaining('task.emits is not supported'),
        }),
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('allows custom Check stages that do not participate in approval evidence', () => {
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
      expect(diagnostics).toEqual([]);
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

  it('rejects catalog check uses that do not have an executable check provider yet', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-workflow-unsupported-check-use-'));
    fs.mkdirSync(path.join(tempDir, '.mohist'));
    fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/unsupported-check-use
  stages:
    - id: check
      tasks: []
      checks:
        - id: pr-merged
          uses: mohist/pr-merged
`, 'utf-8');

    try {
      const diagnostics = validateWorkflowDefinition(resolveWorkflowDefinition(tempDir));
      expect(diagnostics).toEqual([
        expect.objectContaining({
          severity: 'error',
          message: "Use 'mohist/pr-merged' is not supported for full custom check execution yet",
        }),
      ]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});

function toSemanticWorkflowDefinition(definition: WorkflowDefinition): unknown {
  return {
    id: definition.id,
    name: definition.name,
    artifacts: definition.artifacts,
    defaults: definition.defaults,
    stages: definition.stages.map(stage => compact({
      stage: stage.stage,
      tasksFrom: stage.tasksFrom,
      approval: stage.requiresApproval,
      approvalCheckName: stage.approvalCheckName,
      on: stage.on,
      tasks: stage.tasks.map(task => compact({
        id: task.id,
        title: task.title,
        uses: task.uses,
        with: task.with,
        onSuccess: task.onSuccess,
        dependsOn: nonEmpty(task.dependsOn),
        resultContract: task.resultContract,
      })),
      checks: stage.checks.map(check => compact({
        name: check.name,
        title: check.title,
        uses: check.uses,
        with: check.with,
        onFailure: toSemanticOnFailure(check.onFailure),
      })),
    })),
  };
}

function toSemanticOnFailure(onFailure: CheckFailurePolicy | undefined): unknown {
  if (!onFailure?.retry) return undefined;
  return {
    retry: compact({
      limit: onFailure.retry.limit,
      inputFrom: onFailure.retry.inputFrom,
      task: compact({
        id: onFailure.retry.task.id,
        title: onFailure.retry.task.title,
        uses: onFailure.retry.task.uses,
        with: onFailure.retry.task.with,
        onSuccess: onFailure.retry.task.onSuccess,
        dependsOn: nonEmpty(onFailure.retry.task.dependsOn),
        resultContract: onFailure.retry.task.resultContract,
      }),
    }),
  };
}

function nonEmpty<T>(values: T[] | undefined): T[] | undefined {
  return values && values.length > 0 ? values : undefined;
}

function compact<T extends Record<string, unknown>>(value: T): Partial<T> {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)) as Partial<T>;
}
