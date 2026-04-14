import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance, type ToolResult } from '../agent-runtime/tool';

export interface GlobContext {
  projectPath: string;
}

function minimatch(name: string, pattern: string): boolean {
  const regexStr = pattern
    .replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*/g, '.*')
    .replace(/\?/g, '.');
  const regex = new RegExp(`^${regexStr}$`, 'i');
  return regex.test(name);
}

function matchGlob(filePath: string, pattern: string): boolean {
  const parts = pattern.split('/').filter(Boolean);
  const segments = filePath.split('/').filter(Boolean);

  return matchParts(segments, 0, parts, 0);
}

function matchParts(
  segments: string[],
  si: number,
  parts: string[],
  pi: number
): boolean {
  if (pi === parts.length && si === segments.length) return true;
  if (pi === parts.length) return false;

  const part = parts[pi];

  if (part === '**') {
    if (pi === parts.length - 1) return true;
    for (let i = si; i <= segments.length; i++) {
      if (matchParts(segments, i, parts, pi + 1)) return true;
    }
    return false;
  }

  if (si >= segments.length) return false;
  if (!minimatch(segments[si], part)) return false;
  return matchParts(segments, si + 1, parts, pi + 1);
}

function globWalk(dir: string, baseDir: string, pattern: string): string[] {
  const results: string[] = [];

  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return results;
  }

  for (const entry of entries) {
    if (entry.name === 'node_modules' || entry.name === '.git') continue;

    const fullPath = path.join(dir, entry.name);
    const relPath = path.relative(baseDir, fullPath);

    if (entry.isDirectory()) {
      results.push(...globWalk(fullPath, baseDir, pattern));
    } else if (entry.isFile() && matchGlob(relPath, pattern)) {
      results.push(relPath);
    }
  }

  return results;
}

export function createGlobTool(context: GlobContext): ToolInstance<any> {
  return Tool.define('glob', {
    description:
      'Find files matching a glob pattern within the project directory. Uses standard glob patterns (e.g., "**/*.ts", "src/**/*.json"). Returns matching file paths relative to the project root.',
    parameters: z
      .object({
        pattern: z
          .string()
          .describe(
            'Glob pattern to match files (e.g., "**/*.ts", "src/**/*.json")'
          ),
      })
      .strict(),
    execute: async (params): Promise<string | ToolResult> => {
      const MAX_RESULTS = 100;
      const matches = globWalk(context.projectPath, context.projectPath, params.pattern);

      if (matches.length === 0) {
        return 'No files matched the pattern.';
      }

      if (matches.length <= MAX_RESULTS) {
        return matches.join('\n');
      }

      const truncated = matches.slice(0, MAX_RESULTS);
      const output = truncated.join('\n') +
        `\n\n(Results truncated: showing first ${MAX_RESULTS} of ${matches.length} results. Use a more specific path or pattern.)`;

      return {
        output,
        metadata: {
          truncated: true,
          count: matches.length,
        },
      };
    },
  });
}
