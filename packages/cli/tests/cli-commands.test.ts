import { describe, it, expect, vi } from 'vitest';
import { Command } from 'commander';
import { setupProjectCommands } from '../src/cli/commands/project';
import { setupIssueCommands } from '../src/cli/commands/issue';
import { setupQuickCommands } from '../src/cli/commands/quick';

describe('CLI Commands', () => {
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
