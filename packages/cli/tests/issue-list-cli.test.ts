import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupIssueCommands } from '../src/cli/commands/issue';
import { apiClient } from '../src/cli/api-client';

describe('mo issue list CLI', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('--status comma-separated stages', () => {
    it('forwards comma-separated stage values without splitting into multiple requests', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'build,check']).catch(() => {});

      expect(mockedApiClient).toHaveBeenCalledTimes(1);
      const calledPath = mockedApiClient.mock.calls[0][1] as string;
      expect(calledPath).toBe('/issues?stage=build,check');
      expect(exitSpy).not.toHaveBeenCalled();
    });

    it('forwards --status plan,build,check correctly', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--status', 'plan,build,check']).catch(() => {});

      expect(mockedApiClient).toHaveBeenCalledTimes(1);
      const calledPath = mockedApiClient.mock.calls[0][1] as string;
      expect(calledPath).toContain('stage=plan,build,check');
    });
  });

  describe('--status active alias', () => {
    it('forwards active alias to the server without client-side expansion', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 1, title: 'Active Issue', stage: 'build', status: 'active', priority: 'p1', labels: [], projectName: 'demo' }
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'active']).catch(() => {});

      expect(mockedApiClient).toHaveBeenCalledTimes(1);
      const calledPath = mockedApiClient.mock.calls[0][1] as string;
      expect(calledPath).toBe('/issues?stage=active');
    });

    it('displays only returned active pipeline issues', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 1, title: 'Build Issue', stage: 'build', status: 'active', priority: 'p1', labels: [], projectName: 'demo' }
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'active']).catch(() => {});

      expect(logSpy).toHaveBeenCalledWith(expect.stringContaining('Build Issue'));
      expect(logSpy).not.toHaveBeenCalledWith(expect.stringContaining('backlog'));
    });
  });

  describe('--attention flag', () => {
    it('forwards attention=true to the API', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention']).catch(() => {});

      expect(mockedApiClient).toHaveBeenCalledTimes(1);
      const calledPath = mockedApiClient.mock.calls[0][1] as string;
      expect(calledPath).toContain('attention=true');
    });

    it('displays only returned attention issues', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [
          { number: 5, title: 'Blocked Issue', stage: 'build', status: 'blocked', priority: 'p0', labels: [], projectName: 'demo' }
        ],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention']).catch(() => {});

      expect(logSpy).toHaveBeenCalledWith(expect.stringContaining('Attention Issues:'));
      expect(logSpy).toHaveBeenCalledWith(expect.stringContaining('Blocked Issue'));
    });

    it('prints attention-specific empty state when no matches', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention']).catch(() => {});

      expect(logSpy).toHaveBeenCalledWith(expect.stringContaining('No issues requiring attention'));
    });
  });

  describe('API error handling', () => {
    it('exits non-zero and prints error for failed API responses', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: false,
        error: 'Unknown stage or alias: "unknown"',
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '-s', 'unknown']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('Unknown stage or alias'));
      expect(exitSpy).toHaveBeenCalledWith(1);
    });

    it('handles network errors with non-zero exit', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockRejectedValueOnce(new Error('Connection refused'));

      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list']).catch(() => {});

      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('Failed to list issues'));
      expect(exitSpy).toHaveBeenCalledWith(1);
    });
  });

  describe('help text', () => {
    function getIssueListHelp(): string {
      const program = new Command();
      setupIssueCommands(program);
      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue')!;
      const listCmd = issueCmd.commands.find(cmd => cmd.name() === 'list')!;
      return listCmd.helpInformation();
    }

    it('documents --attention flag', async () => {
      const helpText = getIssueListHelp();

      expect(helpText).toContain('--attention');
      expect(helpText).toContain('Show only issues needing user action or decision');
    });

    it('documents comma-separated --status values', async () => {
      const helpText = getIssueListHelp();

      expect(helpText).toContain('comma for multiple');
    });

    it('does NOT document --my flag', async () => {
      const helpText = getIssueListHelp();

      expect(helpText).not.toContain('--my');
    });
  });

  describe('--attention composition with other filters', () => {
    it('sends attention=true and stage together when both provided', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: [],
      } as any);

      const exitSpy = vi.spyOn(process, 'exit').mockImplementation(() => { throw new Error('exit'); });

      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'list', '--attention', '-s', 'build']).catch(() => {});

      expect(mockedApiClient).toHaveBeenCalledTimes(1);
      const calledPath = mockedApiClient.mock.calls[0][1] as string;
      expect(calledPath).toContain('attention=true');
      expect(calledPath).toContain('stage=build');
    });
  });
});