import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { createGlobTool } from '../src/tools/glob-tool';

describe('GlobTool', () => {
  let tmpDir: string;

  beforeEach(async () => {
    tmpDir = path.join(os.tmpdir(), `glob-test-${Date.now()}`);
    await fs.mkdir(tmpDir, { recursive: true });
  });

  afterEach(async () => {
    await fs.rm(tmpDir, { recursive: true, force: true });
  });

  async function createFiles(count: number, ext = '.ts') {
    for (let i = 0; i < count; i++) {
      await fs.writeFile(path.join(tmpDir, `file-${i}${ext}`), '');
    }
  }

  it('should return all matches when under 100 limit', async () => {
    await createFiles(50);
    const tool = createGlobTool({ projectPath: tmpDir });
    const result = await tool.definition.execute({ pattern: '*.ts' });
    const output = typeof result === 'string' ? result : result.output;
    const lines = output.split('\n').filter((l) => l.trim());
    expect(lines).toHaveLength(50);
  });

  it('should truncate at 100 results and include hint', async () => {
    await createFiles(150);
    const tool = createGlobTool({ projectPath: tmpDir });
    const raw = await tool.definition.execute({ pattern: '*.ts' });

    expect(raw).not.toBe(typeof 'string');
    const result = raw as { output: string; metadata?: { truncated?: boolean } };
    expect(result.metadata?.truncated).toBe(true);

    const lines = result.output.split('\n').filter((l) => l.trim());
    const fileLines = lines.filter((l) => !l.startsWith('('));
    expect(fileLines).toHaveLength(100);
    expect(result.output).toContain('Results truncated');
    expect(result.output).toContain('150 results');
  });

  it('should return string when matches are within limit', async () => {
    await createFiles(10);
    const tool = createGlobTool({ projectPath: tmpDir });
    const result = await tool.definition.execute({ pattern: '*.ts' });
    expect(typeof result).toBe('string');
  });

  it('should return no matches message for empty results', async () => {
    const tool = createGlobTool({ projectPath: tmpDir });
    const result = await tool.definition.execute({ pattern: '*.xyz' });
    const output = typeof result === 'string' ? result : result.output;
    expect(output).toContain('No files matched');
  });
});
