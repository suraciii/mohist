import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupIssueCommands } from '../src/cli/commands/issue';
import { apiClient } from '../src/cli/api-client';

describe('mo issue show --compact', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  function mockIssueResponse(overrides: any = {}) {
    return {
      success: true,
      data: {
        number: 215,
        title: 'fix(workflow): make merge-ready prove final integrated code',
        stage: 'build',
        status: 'blocked',
        priority: 'p1',
        labels: ['backend'],
        projectName: 'demo',
        projectPath: '/test/project',
        baseBranch: 'main',
        archivedAt: null,
        body: 'Long body content here',
        comments: [{ id: 'abc123', body: 'A comment', createdAt: '2024-01-01T00:00:00Z' }],
        approvalState: null,
        ...overrides,
      },
    };
  }

  describe('compact output format', () => {
    it('prints one line containing issue number, stage, status, priority, and quoted title', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse() as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']).catch(() => {});

      expect(logSpy).toHaveBeenCalledTimes(1);
      const output = logSpy.mock.calls[0][0] as string;
      expect(output).toBe('#215 build blocked p1 "fix(workflow): make merge-ready prove final integrated code"');
    });

    it('uses issue number without project name prefix', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse({ number: 42 }) as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '42', '--compact']).catch(() => {});

      const output = logSpy.mock.calls[0][0] as string;
      expect(output).toMatch(/^#42 /);
    });

    it('handles missing optional fields gracefully', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: {
          number: 99,
          title: 'Test issue',
          stage: null,
          status: null,
          priority: null,
          projectName: null,
        },
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '99', '--compact']).catch(() => {});

      const output = logSpy.mock.calls[0][0] as string;
      expect(output).toMatch(/^#99 /);
    });
  });

  describe('compact omits long sections', () => {
    it('does NOT fetch coder sessions', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse({
        body: 'x'.repeat(500),
        comments: Array(10).fill({ id: 'abc', body: 'Comment body', createdAt: '2024-01-01T00:00:00Z' }),
      }) as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']).catch(() => {});

      const sessionCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/coder-sessions')
      );
      expect(sessionCall).toBeUndefined();
    });

    it('does NOT fetch executions', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse() as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']).catch(() => {});

      const execCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/executions')
      );
      expect(execCall).toBeUndefined();
    });

    it('does NOT include body in output', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse({
        body: 'This is a very long body that should not appear in compact output',
      }) as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']).catch(() => {});

      const output = logSpy.mock.calls[0][0] as string;
      expect(output).not.toContain('body');
      expect(output).not.toContain('This is a very long body');
    });

    it('does NOT include comments in output', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse({
        comments: [{ id: 'abc123', body: 'A comment that should not appear', createdAt: '2024-01-01T00:00:00Z' }],
      }) as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']).catch(() => {});

      const output = logSpy.mock.calls[0][0] as string;
      expect(output).not.toContain('comment');
      expect(output).not.toContain('A comment');
    });
  });

  describe('default show behavior unchanged', () => {
    it('still fetches coder sessions when --compact is NOT provided', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce(mockIssueResponse() as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215']).catch(() => {});

      const sessionCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/coder-sessions')
      );
      expect(sessionCall).toBeDefined();
    });

    it('still fetches executions when --compact is NOT provided', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce(mockIssueResponse() as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215']).catch(() => {});

      const execCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/executions')
      );
      expect(execCall).toBeDefined();
    });

    it('prints multi-line output without --compact', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce(mockIssueResponse() as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215']).catch(() => {});

      expect(logSpy.mock.calls.length).toBeGreaterThan(5);
    });
  });

  describe('help text', () => {
    function getIssueShowHelp(): string {
      const program = new Command();
      setupIssueCommands(program);
      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue')!;
      const showCmd = issueCmd.commands.find(cmd => cmd.name() === 'show')!;
      return showCmd.helpInformation();
    }

    it('documents --compact option', async () => {
      const helpText = getIssueShowHelp();

      expect(helpText).toContain('--compact');
      expect(helpText).toContain('one-line summary');
    });
  });
});