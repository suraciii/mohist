import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { createReadTasksTool } from '../src/tools/read-tasks';

describe('createReadTasksTool', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function writeTasks(changeDir: string, data: object) {
    const dir = path.join(tmpDir, changeDir);
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'tasks.json'), JSON.stringify(data, null, 2));
  }

  async function executeTool(changePath: string) {
    const tool = createReadTasksTool({ projectPath: tmpDir });
    const parsed = tool.definition.parameters.safeParse({ change_path: changePath });
    if (!parsed.success) {
      return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
    }
    return tool.definition.execute(parsed.data);
  }

  it('should read and format a valid tasks.json', async () => {
    writeTasks('openspec/changes/42-add-auth', {
      version: 1,
      tasks: [
        {
          id: 'T-001',
          order: 1,
          title: 'Setup database',
          description: 'Create database schema',
          acceptanceCriteria: ['Tables created', 'Migrations run'],
          dependsOn: [],
          passes: false,
          attempts: 0,
        },
        {
          id: 'T-002',
          order: 2,
          title: 'Add API',
          description: 'Create REST API endpoints',
          acceptanceCriteria: ['CRUD endpoints work'],
          dependsOn: ['T-001'],
          spec: 'specs/api/spec.md',
          passes: true,
          attempts: 1,
        },
      ],
    });

    const result = await executeTool('openspec/changes/42-add-auth');

    expect(result).toContain('### T-001: Setup database [TODO]');
    expect(result).toContain('### T-002: Add API [PASS]');
    expect(result).toContain('Depends on: T-001');
    expect(result).toContain('Spec: specs/api/spec.md');
    expect(result).toContain('[ ] Tables created');
    expect(result).toContain('[x] CRUD endpoints work');
  });

  it('should return error when tasks.json does not exist', async () => {
    const result = await executeTool('openspec/changes/nonexistent');

    expect(result).toContain('Error: tasks.json not found');
  });

  it('should return error when path is outside project directory', async () => {
    const result = await executeTool('../../etc');

    expect(result).toBe('Error: path is outside the project directory');
  });

  it('should return error for invalid JSON', async () => {
    const dir = path.join(tmpDir, 'broken');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'tasks.json'), '{ not valid json }');

    const result = await executeTool('broken');

    expect(result).toContain('Error: tasks.json contains invalid JSON');
  });

  it('should return error when tasks array is missing', async () => {
    writeTasks('no-tasks', { version: 1 });

    const result = await executeTool('no-tasks');

    expect(result).toContain('Error: tasks.json is missing required "tasks" array');
  });

  it('should handle minimal tasks.json with only required fields', async () => {
    writeTasks('minimal', {
      version: 1,
      tasks: [{ id: 'T-001', order: 1, title: 'Do thing', description: 'Do the thing', passes: false, attempts: 0 }],
    });

    const result = await executeTool('minimal');

    expect(result).toContain('### T-001: Do thing [TODO]');
    expect(result).toContain('Do the thing');
  });

  it('should reject extra parameters', () => {
    const tool = createReadTasksTool({ projectPath: tmpDir });
    const result = tool.definition.parameters.safeParse({
      change_path: 'some/path',
      extra: 'not allowed',
    });

    expect(result.success).toBe(false);
  });

  it('should work with absolute paths inside project', async () => {
    writeTasks('abs-test', {
      version: 1,
      tasks: [{ id: 'T-001', order: 1, title: 'Task', description: 'Desc', passes: false, attempts: 0 }],
    });

    const tool = createReadTasksTool({ projectPath: tmpDir });
    const parsed = tool.definition.parameters.safeParse({
      change_path: path.join(tmpDir, 'abs-test'),
    });
    expect(parsed.success).toBe(true);
    const result = await tool.definition.execute(parsed.data);
    expect(result).toContain('### T-001: Task');
  });
});
