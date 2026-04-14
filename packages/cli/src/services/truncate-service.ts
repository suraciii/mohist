import * as fs from 'fs/promises';
import * as path from 'path';
import * as os from 'os';
import * as crypto from 'crypto';

export interface TruncateResult {
  content: string;
  truncated: boolean;
  outputPath?: string;
}

export interface TruncateOptions {
  maxLines?: number;
  maxBytes?: number;
  direction?: 'head' | 'tail';
}

const DEFAULT_MAX_LINES = 2000;
const DEFAULT_MAX_BYTES = 50 * 1024;

function getToolOutputDir(): string {
  return path.join(os.homedir(), '.mohist', 'tool-output');
}

function generateFileName(): string {
  const ts = Date.now();
  const rand = crypto.randomBytes(4).toString('hex');
  return `tool_${ts}_${rand}.txt`;
}

export async function truncate(
  text: string,
  options?: TruncateOptions,
): Promise<TruncateResult> {
  const maxLines = options?.maxLines ?? DEFAULT_MAX_LINES;
  const maxBytes = options?.maxBytes ?? DEFAULT_MAX_BYTES;
  const direction = options?.direction ?? 'head';

  const lines = text.split('\n');
  const totalBytes = Buffer.byteLength(text, 'utf-8');

  if (lines.length <= maxLines && totalBytes <= maxBytes) {
    return { content: text, truncated: false };
  }

  const out: string[] = [];
  let bytes = 0;
  let hitBytes = false;

  if (direction === 'head') {
    for (let i = 0; i < lines.length && i < maxLines; i++) {
      const size = Buffer.byteLength(lines[i], 'utf-8') + (i > 0 ? 1 : 0);
      if (bytes + size > maxBytes) {
        hitBytes = true;
        break;
      }
      out.push(lines[i]);
      bytes += size;
    }
  } else {
    for (let i = lines.length - 1; i >= 0 && out.length < maxLines; i--) {
      const size = Buffer.byteLength(lines[i], 'utf-8') + (out.length > 0 ? 1 : 0);
      if (bytes + size > maxBytes) {
        hitBytes = true;
        break;
      }
      out.unshift(lines[i]);
      bytes += size;
    }
  }

  const removed = hitBytes ? totalBytes - bytes : lines.length - out.length;
  const unit = hitBytes ? 'bytes' : 'lines';
  const preview = out.join('\n');

  const dir = getToolOutputDir();
  await fs.mkdir(dir, { recursive: true });
  const fileName = generateFileName();
  const outputPath = path.join(dir, fileName);
  await fs.writeFile(outputPath, text, 'utf-8');

  const hint = `The tool call succeeded but the output was truncated. Full output saved to: ${outputPath}\nUse Grep to search the full content or Read with offset/limit to view specific sections.`;

  const content =
    direction === 'head'
      ? `${preview}\n\n...${removed} ${unit} truncated...\n\n${hint}`
      : `...${removed} ${unit} truncated...\n\n${hint}\n\n${preview}`;

  return { content, truncated: true, outputPath };
}
