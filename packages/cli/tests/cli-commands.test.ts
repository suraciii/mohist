import { describe, it, expect, vi, beforeEach } from 'vitest';
import { Command } from 'commander';
import fs from 'fs';
import os from 'os';
import path from 'path';
vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

import { setupProjectCommands } from '../src/cli/commands/project';
import { setupIssueCommands } from '../src/cli/commands/issue';
import { setupQuickCommands } from '../src/cli/commands/quick';
import { setupWorkflowCommands } from '../src/cli/commands/workflow';
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
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'status')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'start')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'retry')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'rerun')).toBe(true);
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

    it.each([
      {
        command: ['issue', 'show', '232'],
        attempt: 'running',
        actions: ['wait', 'stop'],
        absentActions: ['retry'],
      },
      {
        command: ['issue', 'status', '232'],
        attempt: 'failed',
        actions: ['retry', 'rerun stage', 'inspect'],
        absentActions: ['resume'],
      },
      {
        command: ['issue', 'status', '232'],
        attempt: 'interrupted',
        actions: ['resume', 'rerun stage', 'inspect'],
        absentActions: ['retry'],
      },
    ])('renders API recovery projection for $command with $attempt latest attempt', async ({ command, attempt, actions, absentActions }) => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 232,
            title: 'Recovery Issue',
            priority: 'p1',
            stage: 'build',
            status: attempt === 'running' ? 'active' : 'blocked',
            projectName: 'demo',
            baseBranch: 'main',
            labels: [],
            comments: [],
            recovery: {
              workflowSummaryState: attempt === 'running' ? 'running' : 'waiting-for-recovery',
              latestAttemptState: attempt,
              currentWorkItem: { type: 'task', title: 'Implement recovery' },
              allowedActions: actions.includes('rerun stage')
                ? actions.map(action => action === 'rerun stage' ? 'rerun' : action)
                : actions,
            },
          },
        } as any);

      if (command[1] === 'show') {
        mockedApiClient
          .mockResolvedValueOnce({ success: true, data: [] } as any)
          .mockResolvedValueOnce({ success: true, data: [] } as any);
      }

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', ...command]);

      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Recovery:');
      expect(output).toContain('Latest attempt:');
      expect(output).toContain(attempt[0].toUpperCase() + attempt.slice(1));
      for (const action of actions) {
        expect(output).toContain(action);
      }
      for (const action of absentActions) {
        expect(output).not.toContain(action);
      }
      expect(errorSpy).not.toHaveBeenCalled();
    });

    it('recovery commands print refreshed guidance from the same issue projection', async () => {
      const mockedApiClient = vi.mocked(apiClient);
      mockedApiClient
        .mockResolvedValueOnce({
          success: true,
          data: { message: 'Issue #232 retrying from failed work in build stage' },
        } as any)
        .mockResolvedValueOnce({
          success: true,
          data: {
            number: 232,
            title: 'Recovery Issue',
            stage: 'build',
            status: 'active',
            recovery: {
              workflowSummaryState: 'running',
              latestAttemptState: 'running',
              currentWorkItem: { type: 'task', title: 'Implement recovery' },
              allowedActions: ['wait', 'stop'],
            },
          },
        } as any);

      const logSpy = vi.spyOn(console, 'log').mockImplementation(() => {});
      const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const program = new Command();
      setupIssueCommands(program);

      await program.parseAsync(['node', 'test', 'issue', 'retry', '232']);

      expect(mockedApiClient).toHaveBeenNthCalledWith(1, 'POST', '/issues/232/retry');
      expect(mockedApiClient).toHaveBeenNthCalledWith(2, 'GET', '/issues/232');
      const output = logSpy.mock.calls.map(call => call.join(' ')).join('\n');
      expect(output).toContain('Issue #232 retrying from failed work in build stage');
      expect(output).toContain('Recovery:');
      expect(output).toContain('Latest attempt:');
      expect(output).toContain('Running');
      expect(output).toContain('wait');
      expect(output).toContain('stop');
      expect(output).not.toContain('Allowed actions: retry');
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

  describe('Workflow Commands', () => {
    it('should setup workflow inspection commands', () => {
      const program = new Command();
      setupWorkflowCommands(program);

      const workflowCmd = program.commands.find(cmd => cmd.name() === 'workflow');
      expect(workflowCmd).toBeDefined();
      expect(workflowCmd?.commands.some(cmd => cmd.name() === 'show')).toBe(true);
      expect(workflowCmd?.commands.some(cmd => cmd.name() === 'validate')).toBe(true);
      expect(workflowCmd?.commands.some(cmd => cmd.name() === 'explain')).toBe(true);
    });

    it('workflow show renders the expanded default workflow', async () => {
      const lines: string[] = [];
      const program = new Command();
      setupWorkflowCommands(program, { write: line => lines.push(line ?? ''), error: line => lines.push(line) });

      await program.parseAsync(['node', 'test', 'workflow', 'show']);

      const output = lines.join('\n');
      expect(output).toContain('Workflow: mohist/default');
      expect(output).toContain('Task   proposal');
      expect(output).toContain('Check  merge-ready');
      expect(output).toContain('Gate   user-approval');
    });

    it('workflow validate succeeds for the default workflow', async () => {
      const lines: string[] = [];
      const program = new Command();
      setupWorkflowCommands(program, { write: line => lines.push(line ?? ''), error: line => lines.push(line) });

      await program.parseAsync(['node', 'test', 'workflow', 'validate']);

      const output = lines.join('\n');
      expect(output).toContain('Workflow is valid');
      expect(process.exitCode).not.toBe(1);
    });

    it('workflow explain describes a task and a check', async () => {
      const lines: string[] = [];
      const program = new Command();
      setupWorkflowCommands(program, { write: line => lines.push(line ?? ''), error: line => lines.push(line) });

      await program.parseAsync(['node', 'test', 'workflow', 'explain', 'ai-review']);
      await program.parseAsync(['node', 'test', 'workflow', 'explain', 'merge-ready']);

      const output = lines.join('\n');
      expect(output).toContain('Task: ai-review');
      expect(output).toContain('Uses: mohist/agent');
      expect(output).toContain('About: Runs an agent task');
      expect(output).toContain('Check: merge-ready');
      expect(output).toContain('Uses: mohist/merge-ready');
      expect(output).toContain('Blocking: yes');
    });

    it('workflow commands inspect a full custom workflow definition', async () => {
      const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-cli-workflow-custom-'));
      fs.mkdirSync(path.join(tempDir, '.mohist'));
      fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/custom-cli
  stages:
    - id: plan
      tasks:
        - id: design
          uses: mohist/agent
          with:
            prompt: Write design.
      checks:
        - id: design-file
          uses: mohist/artifact-exists
          with:
            path: design.md
`, 'utf-8');
      const cwd = process.cwd();
      const lines: string[] = [];
      const program = new Command();
      setupWorkflowCommands(program, { write: line => lines.push(line ?? ''), error: line => lines.push(line) });

      try {
        process.chdir(tempDir);
        await program.parseAsync(['node', 'test', 'workflow', 'validate']);
        await program.parseAsync(['node', 'test', 'workflow', 'show']);
        await program.parseAsync(['node', 'test', 'workflow', 'explain', 'design']);
      } finally {
        process.chdir(cwd);
        fs.rmSync(tempDir, { recursive: true, force: true });
      }

      const output = lines.join('\n');
      expect(output).toContain('Workflow is valid');
      expect(output).toContain('Workflow:');
      expect(output).toContain('project/custom-cli');
      expect(output).toContain('Task   design');
      expect(output).toContain('source: project');
      expect(output).toContain('Check  design-file');
      expect(output).toContain('Uses: mohist/agent');
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
