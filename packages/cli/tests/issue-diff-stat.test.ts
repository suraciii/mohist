import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupIssueCommands } from '../src/cli/commands/issue';
import { apiClient } from '../src/cli/api-client';

describe('mo issue diff', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  function mockDiffResponse(overrides: any = {}) {
    const defaultFiles = [
      {
        file: 'src/index.ts',
        additions: 10,
        deletions: 5,
        isBinary: false,
        diff: `diff --git a/src/index.ts b/src/index.ts
index 1234567..abcdefg 100644
--- a/src/index.ts
+++ b/src/index.ts
@@ -1,5 +1,6 @@
 const x = 1;
+const y = 2;
 const z = 3;`,
      },
      {
        file: 'README.md',
        additions: 3,
        deletions: 1,
        isBinary: false,
        diff: `diff --git a/README.md b/README.md
index 1111111..2222222 100644
--- a/README.md
+++ b/README.md
@@ -1,3 +1,4 @@
 # Project
+New line
 Old content`,
      },
    ];

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
        summary: {
          filesChanged: 2,
          commits: 0,
          additions: 13,
          deletions: 6,
        },
        files: defaultFiles,
        ...overrides,
      },
    };
  }

  function mockUnavailableResponse(reason: string, message?: string) {
    return {
      success: true,
      data: {
        available: false as const,
        reason,
        message: message || '',
      },
    };
  }

  describe('mo issue diff <id> --stat', () => {
    it('prints file-level additions/deletions and summary without patch hunks', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockDiffResponse() as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(logSpy).toHaveBeenCalled();
      const output = logSpy.mock.calls.map(c => c[0]).join('\n');

      expect(output).toContain('2 file(s)');
      expect(output).toContain('+13');
      expect(output).toContain('-6');
      expect(output).toContain('src/index.ts');
      expect(output).toContain('README.md');
      expect(output).not.toContain('diff --git');
      expect(output).not.toContain('@@');
    });

    it('prints no-changes message when diff is available but zero files changed', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockDiffResponse({
        summary: { filesChanged: 0, commits: 0, additions: 0, deletions: 0 },
        files: [],
      }) as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(logSpy).toHaveBeenCalled();
      const output = logSpy.mock.calls[0][0] as string;
      expect(output).toContain('No changes');
    });

    it('exits non-zero for unavailable diff', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockUnavailableResponse('not_started', 'Issue has not started') as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('unavailable'));
    });
  });

  describe('mo issue diff <id> (default, no --stat)', () => {
    it('prints full patch content from API', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockDiffResponse() as any);

      const stdoutSpy = vi.spyOn(process.stdout, 'write').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215']).catch(() => {});

      expect(stdoutSpy).toHaveBeenCalled();
      const output = stdoutSpy.mock.calls.map(c => c[0]).join('');
      expect(output).toContain('diff --git');
      expect(output).toContain('src/index.ts');
    });

    it('uses same base/head/merge-base semantics as --stat', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      const diffResponse = mockDiffResponse();
      mockedApiClient.mockResolvedValueOnce(diffResponse as any);

      const stdoutSpy = vi.spyOn(process.stdout, 'write').mockImplementation(() => {});

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215']).catch(() => {});

      expect(mockedApiClient).toHaveBeenCalledWith(
        'GET',
        '/issues/215/diff'
      );
    });
  });

  describe('unavailable diff states', () => {
    it('renders distinct feedback for not_started', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockUnavailableResponse('not_started') as any);

      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('not started'));
    });

    it('renders distinct feedback for worktree_removed', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockUnavailableResponse('worktree_removed') as any);

      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('Worktree'));
    });

    it('renders distinct feedback for branch_missing', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockUnavailableResponse('branch_missing') as any);

      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('Branch'));
    });

    it('renders distinct feedback for git_error', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce(mockUnavailableResponse('git_error', 'Check that the branch has commits') as any);

      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'diff', '215', '--stat']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('Git error'));
    });
  });

  describe('help text', () => {
    function getIssueDiffHelp(): string {
      const program = new Command();
      setupIssueCommands(program);
      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue')!;
      const diffCmd = issueCmd.commands.find(cmd => cmd.name() === 'diff')!;
      return diffCmd.helpInformation();
    }

    it('documents --stat option', async () => {
      const helpText = getIssueDiffHelp();
      expect(helpText).toContain('--stat');
      expect(helpText).toContain('statistics');
    });
  });
});