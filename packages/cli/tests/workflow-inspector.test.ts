import { describe, expect, it } from 'vitest';
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
});
