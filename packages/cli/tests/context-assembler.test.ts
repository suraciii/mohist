import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
  buildTaskContext,
  loadLearningsFromDir,
  listLearningFiles,
  formatTaskBlock,
  type Task,
} from '../src/openspec/context-assembler';
import type { OpenSpecChange } from '../src/openspec/detector';
import type { SessionLearning } from '../src/tools/session-memory';

describe('context-assembler', () => {
  let tempDir: string;
  let changeDir: string;
  let change: OpenSpecChange;

  const sampleProposal = '# Proposal\n\nThis is a test proposal.';
  const sampleDesign = '# Design\n\nThis is a test design.';
  const sampleSpec = '# Spec\n\nThis is a test spec.';

  const sampleTask: Task = {
    id: 'T-003',
    order: 3,
    title: 'Implement login API',
    description: 'Create a login endpoint that returns JWT',
    acceptanceCriteria: [
      'POST /api/login returns JWT',
      'Validates email format',
      'Returns 401 for invalid credentials',
    ],
    dependsOn: ['T-001', 'T-002'],
    spec: 'specs/auth/spec.md',
    passes: false,
    attempts: 0,
  };

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-context-test-'));
    changeDir = path.join(tempDir, '42-test-issue');
    fs.mkdirSync(changeDir, { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'specs', 'auth'), { recursive: true });
    fs.mkdirSync(path.join(changeDir, 'session-memories'), { recursive: true });

    fs.writeFileSync(path.join(changeDir, 'proposal.md'), sampleProposal);
    fs.writeFileSync(path.join(changeDir, 'design.md'), sampleDesign);
    fs.writeFileSync(path.join(changeDir, 'specs', 'auth', 'spec.md'), sampleSpec);
    fs.writeFileSync(path.join(changeDir, 'tasks.json'), JSON.stringify({ tasks: [sampleTask] }, null, 2));

    change = {
      changePath: changeDir,
      tasksPath: path.join(changeDir, 'tasks.json'),
      sessionMemoriesPath: path.join(changeDir, 'session-memories'),
      proposalPath: path.join(changeDir, 'proposal.md'),
      designPath: path.join(changeDir, 'design.md'),
      specsPath: path.join(changeDir, 'specs'),
    };
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  describe('formatTaskBlock', () => {
    it('should format task with all fields', () => {
      const result = formatTaskBlock(sampleTask);

      expect(result).toContain('ID: T-003');
      expect(result).toContain('Title: Implement login API');
      expect(result).toContain('Create a login endpoint that returns JWT');
      expect(result).toContain('POST /api/login returns JWT');
      expect(result).toContain('Validates email format');
      expect(result).toContain('Returns 401 for invalid credentials');
      expect(result).toContain('Depends On: T-001, T-002');
    });

    it('should include mode, type, and output when present', () => {
      const taskWithFields: Task = {
        id: 'T-005',
        order: 5,
        title: 'Add migration',
        description: 'Create database migration',
        mode: 'AFK',
        type: 'MIGRATE',
        output: 'migrations/001.sql',
        dependsOn: ['T-001'],
        passes: false,
        attempts: 0,
      };

      const result = formatTaskBlock(taskWithFields);

      expect(result).toContain('Mode: AFK');
      expect(result).toContain('Type: MIGRATE');
      expect(result).toContain('Output: migrations/001.sql');
      expect(result).toContain('Depends On: T-001');
    });

    it('should not include mode/type/output/dependsOn when absent', () => {
      const minimalTask: Task = {
        id: 'T-001',
        title: 'Simple task',
        description: 'A simple task description',
        order: 1,
        passes: false,
        attempts: 0,
      };

      const result = formatTaskBlock(minimalTask);

      expect(result).not.toContain('Mode:');
      expect(result).not.toContain('Type:');
      expect(result).not.toContain('Output:');
      expect(result).not.toContain('Depends On:');
    });

    it('should format task without acceptance criteria', () => {
      const minimalTask: Task = {
        id: 'T-001',
        title: 'Simple task',
        description: 'A simple task description',
        order: 1,
        passes: false,
        attempts: 0,
      };

      const result = formatTaskBlock(minimalTask);

      expect(result).toContain('ID: T-001');
      expect(result).toContain('Title: Simple task');
      expect(result).toContain('A simple task description');
      expect(result).not.toContain('Acceptance Criteria');
    });
  });

  describe('loadLearningsFromDir', () => {
    it('should return empty array when directory does not exist', () => {
      const result = loadLearningsFromDir(path.join(tempDir, 'nonexistent'));
      expect(result).toEqual([]);
    });

    it('should load and sort learnings from directory', () => {
      const learning1: SessionLearning = {
        task_id: 'T-001',
        timestamp: '2024-01-15T10:00:00Z',
        insights: ['First insight'],
        adjustments: [],
        success: true,
        execution_summary: 'First task',
      };

      const learning3: SessionLearning = {
        task_id: 'T-003',
        timestamp: '2024-01-15T12:00:00Z',
        insights: ['Third insight'],
        adjustments: [],
        success: true,
        execution_summary: 'Third task',
      };

      const learning2: SessionLearning = {
        task_id: 'T-002',
        timestamp: '2024-01-15T11:00:00Z',
        insights: ['Second insight'],
        adjustments: [],
        success: true,
        execution_summary: 'Second task',
      };

      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'T-001.json'),
        JSON.stringify(learning1)
      );
      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'T-003.json'),
        JSON.stringify(learning3)
      );
      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'T-002.json'),
        JSON.stringify(learning2)
      );

      const result = loadLearningsFromDir(path.join(changeDir, 'session-memories'));

      expect(result).toHaveLength(3);
      expect(result[0].task_id).toBe('T-001');
      expect(result[1].task_id).toBe('T-002');
      expect(result[2].task_id).toBe('T-003');
    });

    it('should skip invalid JSON files', () => {
      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'valid.json'),
        JSON.stringify({
          task_id: 'T-001',
          timestamp: '2024-01-15T10:00:00Z',
          insights: [],
          adjustments: [],
          success: true,
          execution_summary: 'Valid',
        })
      );
      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'invalid.json'),
        'not valid json'
      );

      const result = loadLearningsFromDir(path.join(changeDir, 'session-memories'));

      expect(result).toHaveLength(1);
      expect(result[0].task_id).toBe('T-001');
    });
  });

  describe('listLearningFiles', () => {
    it('should return empty array when directory does not exist', () => {
      const result = listLearningFiles(path.join(tempDir, 'nonexistent'));
      expect(result).toEqual([]);
    });

    it('should list learning files sorted by name', () => {
      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'T-002.json'),
        JSON.stringify({ task_id: 'T-002', timestamp: '', insights: [], adjustments: [], success: true, execution_summary: '' })
      );
      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'T-001.json'),
        JSON.stringify({ task_id: 'T-001', timestamp: '', insights: [], adjustments: [], success: true, execution_summary: '' })
      );

      const result = listLearningFiles(path.join(changeDir, 'session-memories'));

      expect(result).toHaveLength(2);
      expect(result[0].path).toContain('T-001.json');
      expect(result[1].path).toContain('T-002.json');
      expect(result[0].desc).toContain('T-001');
    });
  });

  describe('buildTaskContext', () => {
    it('should produce XML-structured prompt with context-files for proposal and design', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
        totalTasks: 5,
        issueNumber: 42,
      });

      expect(result.fullPrompt).toContain('<mohist-task>');
      expect(result.fullPrompt).toContain('</mohist-task>');
      expect(result.fullPrompt).toContain('<role>');
      expect(result.fullPrompt).toContain('task T-003 of 5');
      expect(result.fullPrompt).toContain('issue #42');
      expect(result.fullPrompt).toContain('<context-files>');
      expect(result.fullPrompt).toContain(`@${change.proposalPath}`);
      expect(result.fullPrompt).toContain(`@${change.designPath}`);
      expect(result.fullPrompt).toContain(`@${change.tasksPath}`);
      expect(result.fullPrompt).not.toContain(sampleProposal);
      expect(result.fullPrompt).not.toContain(sampleDesign);
    });

    it('should include issue title and body in task context', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        issueNumber: 42,
        issueTitle: 'Add authentication',
        issueBody: 'Users need JWT-based authentication.',
      });

      expect(result.fullPrompt).toContain('Issue #42: Add authentication');
      expect(result.fullPrompt).toContain('Users need JWT-based authentication.');
    });

    it('should inline spec within <spec> tags', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
      });

      expect(result.fullPrompt).toContain('<spec>');
      expect(result.fullPrompt).toContain(sampleSpec);
      expect(result.fullPrompt).toContain('</spec>');
    });

    it('should include <contract> with commit instruction', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
      });

      expect(result.fullPrompt).toContain('<contract>');
      expect(result.fullPrompt).toContain('stage and commit');
      expect(result.fullPrompt).toContain('T-003:');
      expect(result.fullPrompt).toContain('</contract>');
    });

    it('should include <instruction> with build execution strategy', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
      });

      expect(result.fullPrompt).toContain('<instruction>');
      expect(result.fullPrompt).toContain('Before You Start');
      expect(result.fullPrompt).toContain('Read the context-files');
      expect(result.fullPrompt).toContain('</instruction>');
    });

    it('should include <role> with task position info', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
        totalTasks: 8,
        issueNumber: 99,
      });

      expect(result.fullPrompt).toContain('<role>');
      expect(result.fullPrompt).toContain('task T-003 of 8 for issue #99');
      expect(result.fullPrompt).toContain('</role>');
    });

    it('should include learning files as context-files entries', () => {
      const learning: SessionLearning = {
        task_id: 'T-001',
        timestamp: '2024-01-15T10:00:00Z',
        insights: ['Project uses single quotes'],
        adjustments: [],
        success: true,
        execution_summary: 'Completed first task',
      };
      fs.writeFileSync(
        path.join(changeDir, 'session-memories', 'T-001.json'),
        JSON.stringify(learning)
      );

      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [learning],
      });

      expect(result.fullPrompt).toContain('session-memories/T-001.json');
      expect(result.fullPrompt).not.toContain('Project uses single quotes');
    });

    it('should inline retry failure reason within <task>', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
        failureReason: 'Missing validation',
        isRetry: true,
      });

      expect(result.fullPrompt).toContain('<task>');
      expect(result.fullPrompt).toContain('[Previous Attempt Failed]');
      expect(result.fullPrompt).toContain('Failure Reason: Missing validation');
      expect(result.fullPrompt).toContain('</task>');
    });

    it('should inline WIP resume context within <task>', () => {
      const wipContext = 'Modified files:\n- src/index.ts\nDiff summary:\n src/index.ts | 10 +++++-----';
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
        wipResumeContext: wipContext,
      });

      expect(result.fullPrompt).toContain('<task>');
      expect(result.fullPrompt).toContain('[WIP Resume]');
      expect(result.fullPrompt).toContain('Modified files:');
      expect(result.fullPrompt).toContain('Diff summary:');
      expect(result.fullPrompt).toContain('</task>');
    });

    it('should handle missing optional files gracefully', () => {
      fs.rmSync(change.proposalPath);
      fs.rmSync(change.designPath);

      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
      });

      expect(result.proposal).toBeNull();
      expect(result.design).toBeNull();
      expect(result.fullPrompt).toContain('<mohist-task>');
    });

    it('should handle missing spec file', () => {
      const taskWithoutSpec: Task = {
        id: 'T-999',
        title: 'Task without spec',
        description: 'No spec file for this task',
        order: 1,
        passes: false,
        attempts: 0,
      };

      const result = buildTaskContext({
        change,
        task: taskWithoutSpec,
        learnings: [],
      });

      expect(result.spec).toBeNull();
      expect(result.fullPrompt).not.toContain('<spec>');
    });

    it('should handle task with nested spec path', () => {
      fs.mkdirSync(path.join(changeDir, 'specs', 'session-memory'), { recursive: true });
      fs.writeFileSync(
        path.join(changeDir, 'specs', 'session-memory', 'spec.md'),
        '# Session Memory Spec\n\nMemory storage requirements.'
      );

      const taskWithNestedSpec: Task = {
        id: 'T-003',
        title: 'Task with nested spec',
        description: 'Uses nested spec path',
        spec: 'specs/session-memory/spec.md',
        order: 1,
        passes: false,
        attempts: 0,
      };

      const result = buildTaskContext({
        change,
        task: taskWithNestedSpec,
        learnings: [],
      });

      expect(result.spec).toContain('Session Memory Spec');
      expect(result.fullPrompt).toContain('<spec>');
    });
  });
});
