import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import { truncate } from '../src/services/truncate-service';

describe('TruncateService', () => {
  let outputDir: string;

  beforeEach(() => {
    outputDir = path.join(os.homedir(), '.mohist', 'tool-output');
  });

  afterEach(async () => {
    try {
      const files = await fs.readdir(outputDir);
      for (const f of files) {
        if (f.startsWith('tool_')) {
          await fs.unlink(path.join(outputDir, f));
        }
      }
    } catch {
      // dir may not exist
    }
  });

  describe('within limits', () => {
    it('should return content unchanged when within line limit', async () => {
      const text = Array.from({ length: 100 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.truncated).toBe(false);
      expect(result.content).toBe(text);
      expect(result.outputPath).toBeUndefined();
    });

    it('should return content unchanged when exactly at limits', async () => {
      const text = Array.from({ length: 2000 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.truncated).toBe(false);
    });
  });

  describe('exceeds line limit', () => {
    it('should truncate when exceeding line limit', async () => {
      const text = Array.from({ length: 3000 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.truncated).toBe(true);
      expect(result.outputPath).toBeDefined();
      expect(result.content).toContain('1000 lines truncated');
      expect(result.content).toContain('Full output saved to:');
      expect(result.content).toContain('Use Grep to search');

      const lines = result.content.split('\n');
      const previewLines = lines.slice(0, 2000);
      expect(previewLines[0]).toBe('line 0');
      expect(previewLines[1999]).toBe('line 1999');
    });

    it('should write full content to disk', async () => {
      const text = Array.from({ length: 3000 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.outputPath).toBeDefined();
      const written = await fs.readFile(result.outputPath!, 'utf-8');
      expect(written).toBe(text);
    });

    it('should create directory if not exists', async () => {
      await fs.rm(outputDir, { recursive: true, force: true });
      const text = Array.from({ length: 3000 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.truncated).toBe(true);
      const stat = await fs.stat(outputDir);
      expect(stat.isDirectory()).toBe(true);
    });
  });

  describe('exceeds byte limit', () => {
    it('should truncate when exceeding byte limit', async () => {
      const longLine = 'x'.repeat(200);
      const text = Array.from({ length: 300 }, () => longLine).join('\n');
      const totalBytes = Buffer.byteLength(text, 'utf-8');
      const maxBytes = 10000;

      const result = await truncate(text, { maxLines: 2000, maxBytes });

      expect(result.truncated).toBe(true);
      expect(result.outputPath).toBeDefined();
      expect(result.content).toContain('bytes truncated');
      expect(result.content).toContain('Full output saved to:');

      const contentBytes = Buffer.byteLength(
        result.content.split('\n\n...')[0],
        'utf-8',
      );
      expect(contentBytes).toBeLessThanOrEqual(maxBytes);
    });

    it('should write full content to disk on byte truncation', async () => {
      const longLine = 'y'.repeat(300);
      const text = Array.from({ length: 300 }, () => longLine).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 10000 });

      const written = await fs.readFile(result.outputPath!, 'utf-8');
      expect(written).toBe(text);
    });
  });

  describe('direction parameter', () => {
    const text = Array.from({ length: 3000 }, (_, i) => `line ${i}`).join('\n');

    it('should keep head by default', async () => {
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.content).toContain('line 0');
      expect(result.content).toContain('line 1999');
    });

    it('should keep head when direction is head', async () => {
      const result = await truncate(text, {
        maxLines: 2000,
        maxBytes: 51200,
        direction: 'head',
      });

      expect(result.content).toContain('line 0');
      expect(result.content).toContain('line 1999');
    });

    it('should keep tail when direction is tail', async () => {
      const result = await truncate(text, {
        maxLines: 2000,
        maxBytes: 51200,
        direction: 'tail',
      });

      expect(result.content).toContain('line 1000');
      expect(result.content).toContain('line 2999');
    });

    it('should format tail output with hint before preview', async () => {
      const result = await truncate(text, {
        maxLines: 2000,
        maxBytes: 51200,
        direction: 'tail',
      });

      const hintIdx = result.content.indexOf('Full output saved to:');
      const line1000Idx = result.content.indexOf('line 1000');
      expect(hintIdx).toBeLessThan(line1000Idx);
    });
  });

  describe('hint message', () => {
    it('should include truncation amount', async () => {
      const text = Array.from({ length: 3000 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.content).toContain('1000 lines truncated');
    });

    it('should include file path', async () => {
      const text = Array.from({ length: 3000 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.content).toContain(result.outputPath!);
    });

    it('should include read suggestion', async () => {
      const text = Array.from({ length: 3000 }, (_, i) => `line ${i}`).join('\n');
      const result = await truncate(text, { maxLines: 2000, maxBytes: 51200 });

      expect(result.content).toContain('Grep');
      expect(result.content).toContain('offset/limit');
    });
  });
});
