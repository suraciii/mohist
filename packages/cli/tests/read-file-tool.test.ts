import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { createReadFileTool } from '../src/tools/read-file';

describe('ReadFileTool', () => {
  let tmpDir: string;

  beforeEach(async () => {
    tmpDir = path.join(os.tmpdir(), `read-test-${Date.now()}`);
    await fs.mkdir(tmpDir, { recursive: true });
  });

  afterEach(async () => {
    await fs.rm(tmpDir, { recursive: true, force: true });
  });

  async function writeFile(name: string, content: string) {
    await fs.writeFile(path.join(tmpDir, name), content, 'utf-8');
  }

  describe('within limits', () => {
    it('should return full content for small files', async () => {
      const lines = Array.from({ length: 100 }, (_, i) => `line ${i}`);
      await writeFile('small.txt', lines.join('\n'));

      const tool = createReadFileTool({ projectPath: tmpDir });
      const result = await tool.definition.execute({ path: 'small.txt' });
      const output = typeof result === 'string' ? result : result.output;
      expect(output).toContain('line 0');
      expect(output).toContain('line 99');
    });
  });

  describe('line count limit', () => {
    it('should truncate at 2000 lines with hint', async () => {
      const lines = Array.from({ length: 5000 }, (_, i) => `line ${i}`);
      await writeFile('big.txt', lines.join('\n'));

      const tool = createReadFileTool({ projectPath: tmpDir });
      const raw = await tool.definition.execute({ path: 'big.txt' });

      expect(raw).not.toBe(typeof 'string');
      const result = raw as { output: string; metadata?: { truncated?: boolean } };
      expect(result.metadata?.truncated).toBe(true);
      expect(result.output).toContain('line 0');
      expect(result.output).toContain('line 1999');
      expect(result.output).toContain('offset=2001');
    });

    it('should not truncate when explicit offset and limit are provided', async () => {
      const lines = Array.from({ length: 5000 }, (_, i) => `line ${i}`);
      await writeFile('big.txt', lines.join('\n'));

      const tool = createReadFileTool({ projectPath: tmpDir });
      const result = await tool.definition.execute({ path: 'big.txt', offset: 1, limit: 3000 });
      const output = typeof result === 'string' ? result : result.output;
      expect(output).toContain('line 0');
      expect(output).toContain('line 2999');
    });
  });

  describe('byte limit', () => {
    it('should truncate when exceeding 50KB', async () => {
      const longLine = 'x'.repeat(300);
      const lines = Array.from({ length: 300 }, () => longLine);
      await writeFile('fat.txt', lines.join('\n'));

      const tool = createReadFileTool({ projectPath: tmpDir });
      const raw = await tool.definition.execute({ path: 'fat.txt' });

      expect(raw).not.toBe(typeof 'string');
      const result = raw as { output: string; metadata?: { truncated?: boolean } };
      expect(result.metadata?.truncated).toBe(true);
      expect(result.output).toContain('50KB');
    });
  });

  describe('line length limit', () => {
    it('should truncate individual lines exceeding 2000 chars', async () => {
      const lines = ['short', 'x'.repeat(5000), 'end'];
      await writeFile('longline.txt', lines.join('\n'));

      const tool = createReadFileTool({ projectPath: tmpDir });
      const raw = await tool.definition.execute({ path: 'longline.txt' });

      const result = raw as { output: string; metadata?: { truncated?: boolean; lineTruncations?: number[] } };
      expect(result.metadata?.truncated).toBe(true);
      expect(result.metadata?.lineTruncations).toBeDefined();
      expect(result.output).toContain('line truncated to 2000 chars');
    });
  });

  describe('errors', () => {
    it('should return error for missing file', async () => {
      const tool = createReadFileTool({ projectPath: tmpDir });
      const result = await tool.definition.execute({ path: 'nonexistent.txt' });
      const output = typeof result === 'string' ? result : result.output;
      expect(output).toContain('file not found');
    });

    it('should return error for path outside project', async () => {
      const tool = createReadFileTool({ projectPath: tmpDir });
      const result = await tool.definition.execute({ path: '../../etc/passwd' });
      const output = typeof result === 'string' ? result : result.output;
      expect(output).toContain('outside the project directory');
    });
  });
});
