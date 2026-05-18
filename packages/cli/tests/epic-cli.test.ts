import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';

vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupEpicCommands } from '../src/cli/commands/epic';
import { apiClient } from '../src/cli/api-client';

describe('mo epic CLI', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('lists every epic with status, progress, and next state', async () => {
    const mockedApiClient = vi.mocked(apiClient);
    mockedApiClient.mockResolvedValueOnce({
      success: true,
      data: [
        {
          id: 'active-epic',
          title: 'Active Epic',
          status: 'active',
          priority: 'p1',
          description: 'active',
          createdAt: '2026-01-01T00:00:00.000Z',
          updatedAt: '2026-01-01T00:00:00.000Z',
          progress: {
            deliveredCount: 1,
            totalIssueCount: 3,
            blockedIssues: [],
            activeIssues: ['issue-2'],
            nextIssue: { id: 'issue-2', number: 2, title: 'Continue active work' },
            readyToMarkDone: false,
          },
        },
        {
          id: 'done-epic',
          title: 'Done Epic',
          status: 'done',
          priority: 'p2',
          description: 'done',
          createdAt: '2026-01-01T00:00:00.000Z',
          updatedAt: '2026-01-01T00:00:00.000Z',
          progress: {
            deliveredCount: 2,
            totalIssueCount: 2,
            blockedIssues: [],
            activeIssues: [],
            nextIssue: null,
            readyToMarkDone: true,
          },
        },
        {
          id: 'closed-epic',
          title: 'Closed Epic',
          status: 'closed',
          priority: 'p3',
          description: 'closed',
          createdAt: '2026-01-01T00:00:00.000Z',
          updatedAt: '2026-01-01T00:00:00.000Z',
          progress: {
            deliveredCount: 0,
            totalIssueCount: 0,
            blockedIssues: [],
            activeIssues: [],
            nextIssue: null,
            readyToMarkDone: false,
          },
        },
      ],
    } as any);

    const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

    const program = new Command();
    setupEpicCommands(program);

    await program.parseAsync(['node', 'test', 'epic', 'list']);

    const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
    expect(output).toContain('Active Epic');
    expect(output).toContain('status: active');
    expect(output).toContain('1/3 delivered');
    expect(output).toContain('next: #2 Continue active work');
    expect(output).toContain('Done Epic');
    expect(output).toContain('status: done');
    expect(output).toContain('2/2 delivered');
    expect(output).toContain('next: ready to mark done');
    expect(output).toContain('Closed Epic');
    expect(output).toContain('status: closed');
    expect(output).toContain('0/0 delivered');
    expect(output).toContain('next: none');
    expect(errorSpy).not.toHaveBeenCalled();
    expect(exitSpy).not.toHaveBeenCalled();
  });
});
