import { describe, expect, it, vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { Stage, IssueStatus } from '../../../../src/types';
import { createOpenSpecTaskLoader } from '../../../../src/workflow/builtins/tasks';
import type { StageContext } from '../../../../src/workflow/stage-context';
import { createWorkflowDefinitionSnapshot } from '@mohist/workflow/internal/model';

function makeContext(worktreePath: string, changeDir: string): StageContext {
  const workflowDefinition = createWorkflowDefinitionSnapshot({
    definition: {
      id: 'test/openspec-source',
      artifacts: {
        openspecChange: 'openspec/changes/159-test-issue',
      },
      stages: [{
        stage: Stage.Build,
        tasks: [],
        tasksFrom: {
          uses: 'mohist/openspec-tasks',
          with: {
            path: '{{ artifacts.openspecChange }}/tasks.json',
            task: {
              uses: 'mohist/agent',
              with: {
                session: 'build',
                prompt: {
                  inline: [
                    '<task>',
                    '  <id>{{ task.id }}</id>',
                    '  <title>{{ task.title }}</title>',
                    '  <description>{{ task.description }}</description>',
                    '  <acceptanceCriteria>',
                    '{{ task.acceptanceCriteria }}',
                    '  </acceptanceCriteria>',
                    '</task>',
                  ].join('\n'),
                },
              },
            },
          },
        },
        checks: [],
      }],
    },
    capturedAt: '2026-05-21T00:00:00.000Z',
  });

  return {
    issue: {
      id: 'issue-1',
      number: 159,
      title: 'Test Issue',
      body: 'Test body',
      stage: Stage.Build,
      status: IssueStatus.Active,
      projectId: 'project-1',
      labels: [],
      priority: 'p1',
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    acpOptions: { cwd: worktreePath } as any,
    artifactManager: {
      getChangeDir: vi.fn().mockReturnValue(changeDir),
      createChangeDir: vi.fn().mockReturnValue(changeDir),
    } as any,
    worktreeManager: {} as any,
    projectRepo: {} as any,
    eventBus: { emit: vi.fn() } as any,
    checkpointManager: {} as any,
    issueRepo: {} as any,
    workflowRun: { workflowDefinition } as any,
    workflowRunService: undefined,
    workflowApplicationService: undefined,
    requestedWork: undefined,
    requestedTask: undefined,
    emit: vi.fn(),
    log: vi.fn(),
  } as StageContext;
}

describe('createOpenSpecTaskLoader', () => {
  it('materializes OpenSpec tasks.json entries into ordinary mohist/agent tasks with interpolated prompts', () => {
    const worktreePath = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-openspec-loader-'));
    try {
      const changeDir = path.join(worktreePath, 'openspec', 'changes', '159-test-issue');
      fs.mkdirSync(changeDir, { recursive: true });
      fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({
        tasks: [{
          id: 'T-001',
          order: 1,
          title: 'Implement feature',
          description: 'Create the feature code.',
          acceptanceCriteria: ['Feature works', 'Regression test exists'],
        }],
      }));

      const tasks = createOpenSpecTaskLoader().load(makeContext(worktreePath, changeDir));

      expect(tasks).toHaveLength(1);
      expect(tasks[0]).toMatchObject({
        taskId: 'T-001',
        title: 'Implement feature',
        uses: 'mohist/agent',
        input: {
          session: 'build',
          prompt: {
            inline: expect.stringContaining('<task>'),
          },
        },
      });
      const prompt = (tasks[0].input as any).prompt.inline;
      expect(prompt).toContain('<id>T-001</id>');
      expect(prompt).toContain('<title>Implement feature</title>');
      expect(prompt).toContain('- Feature works');
      expect(prompt).toContain('- Regression test exists');
    } finally {
      fs.rmSync(worktreePath, { recursive: true, force: true });
    }
  });
});
