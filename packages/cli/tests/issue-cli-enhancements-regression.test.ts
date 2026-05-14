import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupIssueCommands } from '../src/cli/commands/issue';
import { apiClient } from '../src/cli/api-client';

describe('Issue CLI Enhancements Regression', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('active alias list excludes backlog issues', () => {
    it('returns only pipeline issues when using -s active', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 1, title: 'Plan Issue', stage: 'plan', status: 'active', priority: 'p1', labels: [], projectName: 'demo' },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'active']);

      expect(mockedApiClient).toHaveBeenCalledWith('GET', '/issues?stage=active');
      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('Plan Issue');
      expect(output).not.toContain('backlog');
    });

    it('does not return backlog active issues', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'active']);

      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('No issues found');
    });
  });

  describe('attention filter', () => {
    it('returns blocked and interrupted issues', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 10, title: 'Blocked Issue', stage: 'build', status: 'blocked', priority: 'p1', labels: [], projectName: 'demo' },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention']);

      expect(mockedApiClient).toHaveBeenCalledWith('GET', '/issues?attention=true');
      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('Attention Issues:');
      expect(output).toContain('Blocked Issue');
    });

    it('does not include normal running issues', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention']).catch(() => {});

      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('No issues requiring attention');
    });

    it('shows attention empty state', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention']);

      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('No issues requiring attention');
    });

    it('combines with stage filter', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 30, title: 'Build Blocked', stage: 'build', status: 'blocked', priority: 'p1', labels: [], projectName: 'demo' },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention', '-s', 'build']);

      const callPath = mockedApiClient.mock.calls[0][1] as string;
      expect(callPath).toContain('attention=true');
      expect(callPath).toContain('stage=build');
      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('Build Blocked');
    });
  });

  describe('invalid stage input exits non-zero', () => {
    it('handles unknown stage with error', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: false,
        error: 'Unknown stage or alias: "unknown"',
      } as any);

      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'unknown']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('Unknown stage or alias'));
      expect(exitSpy).toHaveBeenCalledWith(1);
    });
  });

  describe('compact show', () => {
    function mockIssueResponse() {
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
          body: 'Long body content that should not appear in compact output',
          comments: [{ id: 'abc123', body: 'A comment', createdAt: '2024-01-01T00:00:00Z' }],
          approvalState: null,
        },
      };
    }

    it('outputs exactly one line with --compact', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse() as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']);

      expect(logSpy).toHaveBeenCalledTimes(1);
      const output = logSpy.mock.calls[0][0] as string;
      expect(output).toMatch(/^#215 build blocked p1 "fix\(workflow\): make merge-ready prove final integrated code"$/);
    });

    it('omits body and comments in compact mode', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse() as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']);

      const output = logSpy.mock.calls[0][0] as string;
      expect(output).not.toContain('Long body content');
      expect(output).not.toContain('A comment');
    });

    it('does not fetch sessions or executions in compact mode', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockIssueResponse() as any);

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215', '--compact']);

      const sessionsCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/coder-sessions')
      );
      const executionsCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/executions')
      );
      expect(sessionsCall).toBeUndefined();
      expect(executionsCall).toBeUndefined();
    });

    it('default show fetches sessions and executions', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce(mockIssueResponse() as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any)
        .mockResolvedValueOnce({ success: true, data: [] } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '215']);

      const sessionsCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/coder-sessions')
      );
      const executionsCall = mockedApiClient.mock.calls.find(([method, path]) =>
        method === 'GET' && path.includes('/executions')
      );
      expect(sessionsCall).toBeDefined();
      expect(executionsCall).toBeDefined();
    });
  });

  describe('diff --stat', () => {
    function mockDiffResponse() {
      return {
        success: true,
        data: {
          available: true,
          reason: null,
          base: 'main',
          head: 'mo/issue-215',
          mergeBase: 'abc1234',
          ahead: 5,
          behind: 0,
          canFastForward: true,
          comparison: 'merge-base' as const,
          summary: { filesChanged: 2, commits: 0, additions: 13, deletions: 6 },
          files: [
            { file: 'src/index.ts', additions: 10, deletions: 5, isBinary: false,
              diff: 'diff --git a/src/index.ts b/src/index.ts\nindex 1234567..abcdefg 100644\n--- a/src/index.ts\n+++ b/src/index.ts\n@@ -1,5 +1,6 @@\n const x = 1;\n+const y = 2;\n const z = 3;' },
            { file: 'README.md', additions: 3, deletions: 1, isBinary: false,
              diff: 'diff --git a/README.md b/README.md\nindex 1111111..2222222 100644\n--- a/README.md\n+++ b/README.md\n@@ -1,3 +1,4 @@\n # Project\n+New line\n Old content' },
          ],
        },
      };
    }

    it('outputs file stats without patch hunks with --stat', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockDiffResponse() as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('2 file(s)');
      expect(output).toContain('+13');
      expect(output).toContain('-6');
      expect(output).toContain('src/index.ts');
      expect(output).not.toContain('diff --git');
    });

    it('default diff outputs full patch', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockDiffResponse() as any);

      const stdoutSpy = vi.spyOn(process.stdout, 'write').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215']);

      const output = stdoutSpy.mock.calls.map(c => c[0]).join('');
      expect(output).toContain('diff --git');
      expect(output).toContain('+const y = 2;');
    });

    it('exits non-zero for unavailable diff', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: { available: false, reason: 'not_started', message: 'Issue has not started' },
      } as any);

      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('not started'));
      expect(exitSpy).toHaveBeenCalledWith(1);
    });
  });

  describe('help text', () => {
    function getListHelp() {
      const program = new Command();
      setupIssueCommands(program);
      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue')!;
      const listCmd = issueCmd.commands.find(cmd => cmd.name() === 'list')!;
      return listCmd.helpInformation();
    }

    function getShowHelp() {
      const program = new Command();
      setupIssueCommands(program);
      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue')!;
      const showCmd = issueCmd.commands.find(cmd => cmd.name() === 'show')!;
      return showCmd.helpInformation();
    }

    function getDiffHelp() {
      const program = new Command();
      setupIssueCommands(program);
      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue')!;
      const diffCmd = issueCmd.commands.find(cmd => cmd.name() === 'diff')!;
      return diffCmd.helpInformation();
    }

    it('documents --attention in list help', () => {
      const helpText = getListHelp();
      expect(helpText).toContain('--attention');
    });

    it('documents comma-separated status in list help', () => {
      const helpText = getListHelp();
      expect(helpText).toContain('comma');
    });

    it('does not document --my', () => {
      const helpText = getListHelp();
      expect(helpText).not.toContain('--my');
    });

    it('documents --compact in show help', () => {
      const helpText = getShowHelp();
      expect(helpText).toContain('--compact');
    });

    it('documents --stat in diff help', () => {
      const helpText = getDiffHelp();
      expect(helpText).toContain('--stat');
    });
  });

  describe('multi-stage filter', () => {
    it('handles comma-separated stages', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 40, title: 'Build Issue', stage: 'build', status: 'active', priority: 'p2', labels: [], projectName: 'demo' },
          { number: 41, title: 'Check Issue', stage: 'check', status: 'active', priority: 'p2', labels: [], projectName: 'demo' },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'build,check']);

      expect(mockedApiClient).toHaveBeenCalledWith('GET', '/issues?stage=build,check');
      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('Build Issue');
      expect(output).toContain('Check Issue');
    });

    it('handles --status long form', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--status', 'plan,build,check']);

      expect(mockedApiClient).toHaveBeenCalledWith('GET', '/issues?stage=plan,build,check');
    });
  });

  describe('stage composition with filters', () => {
    it('stage composes with priority', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 50, title: 'Build P1', stage: 'build', status: 'blocked', priority: 'p1', labels: [], projectName: 'demo' },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'build', '-p', 'p1']);

      expect(mockedApiClient).toHaveBeenCalledWith('GET', '/issues?stage=build&priority=p1');
      const output = logSpy.mock.calls.map(c => c[0]).join('\n');
      expect(output).toContain('Build P1');
    });

    it('stage composes with archived', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 51, title: 'Archived Build', stage: 'build', status: 'active', priority: 'p2', labels: [], projectName: 'demo', archivedAt: '2024-01-01T00:00:00Z' },
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'build', '--archived']);

      expect(mockedApiClient).toHaveBeenCalledWith('GET', '/issues?stage=build&archived=true');
    });
  });

  describe('default behaviors unchanged', () => {
    it('mo issue list without filters uses /issues', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list']);

      expect(mockedApiClient).toHaveBeenCalledWith('GET', '/issues');
    });
  });
});