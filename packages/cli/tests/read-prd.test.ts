import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { createReadPrdTool } from '../src/tools/read-prd';

describe('createReadPrdTool', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function writePrd(changeDir: string, data: object) {
    const dir = path.join(tmpDir, changeDir);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'prd.json'), JSON.stringify(data, null, 2));
  }

  async function executeTool(changePath: string) {
    const tool = createReadPrdTool({ projectPath: tmpDir });
    const parsed = tool.definition.parameters.safeParse({ change_path: changePath });
    if (!parsed.success) {
      return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
    }
    return tool.definition.execute(parsed.data);
  }

  it('should read and format a valid prd.json', async () => {
    writePrd('.mohist-specs/changes/42-add-auth', {
      version: '1.0',
      change_id: '42-add-auth',
      issue_reference: 'Issue #42',
      generated_at: '2024-01-01T00:00:00Z',
      tasks: [
        {
          id: 'T-001',
          order: 1,
          title: 'Setup database',
          description: 'Create database schema',
          acceptance_criteria: ['Tables created', 'Migrations run'],
          dependencies: [],
          estimated_effort: 'small',
        },
        {
          id: 'T-002',
          order: 2,
          title: 'Add API',
          description: 'Create REST API endpoints',
          acceptance_criteria: ['CRUD endpoints work'],
          dependencies: ['T-001'],
          spec_file: 'specs/api/spec.md',
        },
      ],
      metadata: {
        total_tasks: 2,
        capabilities_covered: ['db', 'api'],
        session_memory_path: './session-memories/',
        task_status_path: './task-status.json',
      },
    });

    const result = await executeTool('.mohist-specs/changes/42-add-auth');

    expect(result).toContain('# PRD: 42-add-auth');
    expect(result).toContain('Issue Reference: Issue #42');
    expect(result).toContain('### T-001: Setup database');
    expect(result).toContain('### T-002: Add API');
    expect(result).toContain('Dependencies: T-001');
    expect(result).toContain('Spec: specs/api/spec.md');
    expect(result).toContain('[ ] Tables created');
    expect(result).toContain('[ ] CRUD endpoints work');
    expect(result).toContain('Capabilities: db, api');
    expect(result).toContain('Session memory path: ./session-memories/');
  });

  it('should return error when prd.json does not exist', async () => {
    const result = await executeTool('.mohist-specs/changes/nonexistent');

    expect(result).toContain('Error: prd.json not found');
  });

  it('should return error when path is outside project directory', async () => {
    const result = await executeTool('../../etc');

    expect(result).toBe('Error: path is outside the project directory');
  });

  it('should return error for invalid JSON', async () => {
    const dir = path.join(tmpDir, 'broken');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'prd.json'), '{ not valid json }');

    const result = await executeTool('broken');

    expect(result).toContain('Error: prd.json contains invalid JSON');
  });

  it('should return error when tasks array is missing', async () => {
    writePrd('no-tasks', { version: '1.0' });

    const result = await executeTool('no-tasks');

    expect(result).toContain('Error: prd.json is missing required "tasks" array');
  });

  it('should handle minimal prd.json with only tasks', async () => {
    writePrd('minimal', {
      tasks: [{ id: 'T-001', title: 'Do thing', description: 'Do the thing' }],
    });

    const result = await executeTool('minimal');

    expect(result).toContain('# PRD: unknown');
    expect(result).toContain('### T-001: Do thing');
    expect(result).toContain('Do the thing');
  });

  it('should reject extra parameters', () => {
    const tool = createReadPrdTool({ projectPath: tmpDir });
    const result = tool.definition.parameters.safeParse({
      change_path: 'some/path',
      extra: 'not allowed',
    });

    expect(result.success).toBe(false);
  });

  it('should work with absolute paths inside project', async () => {
    writePrd('abs-test', {
      tasks: [{ id: 'T-001', title: 'Task', description: 'Desc' }],
    });

    const tool = createReadPrdTool({ projectPath: tmpDir });
    const parsed = tool.definition.parameters.safeParse({
      change_path: path.join(tmpDir, 'abs-test'),
    });
    expect(parsed.success).toBe(true);
    const result = await tool.definition.execute(parsed.data);
    expect(result).toContain('### T-001: Task');
  });
});
