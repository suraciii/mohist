import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupProjectCommands } from '../src/cli/commands/project';
import { setupIssueCommands } from '../src/cli/commands/issue';
import { setupQuickCommands } from '../src/cli/commands/quick';
import { apiClient } from '../src/cli/api-client';

describe('CLI Commands', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('Project Commands', () => {
    it('should setup project commands', () => {
      const program = new Command();
      setupProjectCommands(program);
      
      const commands = program.commands;
      expect(commands.some(cmd => cmd.name() === 'project')).toBe(true);
      
      const projectCmd = commands.find(cmd => cmd.name() === 'project');
      expect(projectCmd?.commands.some(cmd => cmd.name() === 'create')).toBe(true);
      expect(projectCmd?.commands.some(cmd => cmd.name() === 'list')).toBe(true);
      expect(projectCmd?.commands.some(cmd => cmd.name() === 'use')).toBe(true);
      expect(projectCmd?.commands.some(cmd => cmd.name() === 'remove')).toBe(true);
      expect(projectCmd?.commands.some(cmd => cmd.name() === 'show')).toBe(true);
    });
  });
  
  describe('Issue Commands', () => {
    it('should setup issue commands', () => {
      const program = new Command();
      setupIssueCommands(program);

      const commands = program.commands;
      expect(commands.some(cmd => cmd.name() === 'issue')).toBe(true);

      const issueCmd = commands.find(cmd => cmd.name() === 'issue');
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'create')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'list')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'show')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'start')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'close')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'reopen')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'comment')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'delete-comment')).toBe(true);
    });

    it('should setup comment and delete-comment subcommands', () => {
      const program = new Command();
      setupIssueCommands(program);

      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');

      const commentCmd = issueCmd?.commands.find(cmd => cmd.name() === 'comment');
      expect(commentCmd).toBeDefined();
      expect(commentCmd?.name()).toBe('comment');

      const deleteCommentCmd = issueCmd?.commands.find(cmd => cmd.name() === 'delete-comment');
      expect(deleteCommentCmd).toBeDefined();
      expect(deleteCommentCmd?.name()).toBe('delete-comment');
    });

    it('issue show renders only the latest ai-review truth per stage', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 1,
            title: 'Test Issue',
            priority: 'p2',
            stage: 'check',
            status: 'active',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
          },
        } as any)
        .mockResolvedValueOnce({
          success: true,
          data: [],
        } as any)
        .mockResolvedValueOnce({
          success: true,
          data: [
            {
              stage: 'check',
              checkResults: [
                { name: 'review-passed', status: 'fail', message: 'old fail' },
                { name: 'review-passed', status: 'pass', message: 'new pass' },
                { name: 'user-approval', status: 'pending', message: 'awaiting' },
              ],
            },
          ],
        } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'show', '1']);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output.match(/review-passed/g)?.length ?? 0).toBe(1);
      expect(output).toContain('review-passed');
      expect(output).toContain('user-approval');
      expect(errorSpy).not.toHaveBeenCalled();
    });

    it('issue update omits priority when --priority is not provided', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient.mockResolvedValueOnce({
        success: true,
        data: {
          number: 199,
          title: 'Updated Issue',
          priority: 'p1',
          stage: 'backlog',
          status: 'active',
          labels: [],
        },
      } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'update', '199', '--body', 'new body']);

      expect(mockedApiClient).toHaveBeenCalledWith(
        'PATCH',
        '/issues/199',
        expect.not.objectContaining({ priority: expect.anything() })
      );
      expect(mockedApiClient.mock.calls[0][2]).toEqual({
        title: undefined,
        body: 'new body',
        addLabels: undefined,
        removeLabels: undefined,
      });
      expect(logSpy).toHaveBeenCalledWith(expect.stringContaining('Updated issue #199'));
      expect(errorSpy).not.toHaveBeenCalled();
    });
  });
  
  describe('Quick Commands', () => {
    it('should setup quick commands', () => {
      const program = new Command();
      setupQuickCommands(program);
      
      const commands = program.commands;
      expect(commands.some(cmd => cmd.name() === 'status')).toBe(true);
      expect(commands.some(cmd => cmd.name() === 'config')).toBe(true);
    });
  });
  
  describe('Command Options', () => {
    it('issue list should support --status option', () => {
      const program = new Command();
      setupIssueCommands(program);
      
      const issueCmd = program.commands.find(cmd => cmd.name() === 'issue');
      const listCmd = issueCmd?.commands.find(cmd => cmd.name() === 'list');
      
      expect(listCmd?.options.some(opt => opt.long === '--status')).toBe(true);
    });
    
    it('status should support --all option', () => {
      const program = new Command();
      setupQuickCommands(program);
      
      const statusCmd = program.commands.find(cmd => cmd.name() === 'status');
      
      expect(statusCmd?.options.some(opt => opt.long === '--all')).toBe(true);
    });
  });
});
