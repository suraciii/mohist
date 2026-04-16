import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
  buildTaskContext,
  loadLearningsFromDir,
  formatLearningsForPrompt,
  formatTaskForPrompt,
  formatRetryContext,
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

  describe('formatTaskForPrompt', () => {
    it('should format task with all fields', () => {
      const result = formatTaskForPrompt(sampleTask);

      expect(result).toContain('[Task T-003]');
      expect(result).toContain('Title: Implement login API');
      expect(result).toContain('Description: Create a login endpoint that returns JWT');
      expect(result).toContain('POST /api/login returns JWT');
      expect(result).toContain('Validates email format');
      expect(result).toContain('Returns 401 for invalid credentials');
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

      const result = formatTaskForPrompt(minimalTask);

      expect(result).toContain('[Task T-001]');
      expect(result).toContain('Title: Simple task');
      expect(result).toContain('Description: A simple task description');
      expect(result).not.toContain('Acceptance Criteria');
    });
  });

  describe('formatLearningsForPrompt', () => {
    it('should return empty string for empty learnings', () => {
      const result = formatLearningsForPrompt([]);
      expect(result).toBe('');
    });

    it('should format successful learnings', () => {
      const learnings: SessionLearning[] = [
        {
          task_id: 'T-001',
          timestamp: '2024-01-15T10:30:00Z',
          insights: ['Project uses single quotes'],
          adjustments: [],
          success: true,
          execution_summary: 'Implemented login UI',
        },
      ];

      const result = formatLearningsForPrompt(learnings);

      expect(result).toContain('[Previous Task Learnings]');
      expect(result).toContain('From T-001:');
      expect(result).toContain('"Implemented login UI"');
      expect(result).toContain('Insights: Project uses single quotes');
    });

    it('should format failed learnings with failure reason', () => {
      const learnings: SessionLearning[] = [
        {
          task_id: 'T-002',
          timestamp: '2024-01-15T11:30:00Z',
          insights: [],
          adjustments: ['Add backend validation'],
          success: false,
          execution_summary: 'Tried to implement validation',
          failure_reason: 'Only frontend validation implemented',
        },
      ];

      const result = formatLearningsForPrompt(learnings);

      expect(result).toContain('From T-002:');
      expect(result).toContain('Failed: "Only frontend validation implemented"');
      expect(result).toContain('Adjustments: Add backend validation');
    });
  });

  describe('formatRetryContext', () => {
    it('should format retry context with failure reason', () => {
      const result = formatRetryContext('Missing backend validation', sampleTask);

      expect(result).toContain('[Previous Attempt Failed]');
      expect(result).toContain('Failure Reason: Missing backend validation');
      expect(result).toContain('[Task]');
      expect(result).toContain('T-003');
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

  describe('buildTaskContext', () => {
    it('should assemble complete context with all components', () => {
      const learnings: SessionLearning[] = [
        {
          task_id: 'T-001',
          timestamp: '2024-01-15T10:00:00Z',
          insights: ['Project uses single quotes'],
          adjustments: [],
          success: true,
          execution_summary: 'Completed first task',
        },
      ];

      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings,
      });

      expect(result.proposal).toBe(sampleProposal);
      expect(result.design).toBe(sampleDesign);
      expect(result.spec).toBe(sampleSpec);
      expect(result.learnings).toEqual(learnings);
      expect(result.fullPrompt).toContain('[Proposal]');
      expect(result.fullPrompt).toContain('[Design]');
      expect(result.fullPrompt).toContain('[Current Requirement: specs/auth/spec.md]');
      expect(result.fullPrompt).toContain('[Previous Task Learnings]');
      expect(result.fullPrompt).toContain('From T-001:');
    });

    it('should assemble context with retry failure context', () => {
      const result = buildTaskContext({
        change,
        task: sampleTask,
        learnings: [],
        failureReason: 'Missing validation',
        isRetry: true,
      });

      expect(result.fullPrompt).toContain('[Previous Attempt Failed]');
      expect(result.fullPrompt).toContain('Failure Reason: Missing validation');
      expect(result.fullPrompt).toContain('[Task]');
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
      expect(result.fullPrompt).not.toContain('[Proposal]');
      expect(result.fullPrompt).not.toContain('[Design]');
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
      expect(result.fullPrompt).not.toContain('[Current Requirement]');
    });

    it('should work with task that has spec path with subdirectories', () => {
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
      expect(result.fullPrompt).toContain('session-memory/spec.md');
    });
  });
});
