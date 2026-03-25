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
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'approve')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'pause')).toBe(true);
      expect(issueCmd?.commands.some(cmd => cmd.name() === 'resume')).toBe(true);
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
