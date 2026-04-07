import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

export interface GrepContext {
  projectPath: string;
}

function walkFiles(dir: string, include?: string): string[] {
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
    if (entry.isDirectory()) {
      results.push(...walkFiles(fullPath, include));
    } else if (entry.isFile()) {
      if (include && !entry.name.endsWith(include)) continue;
      results.push(fullPath);
    }
  }

  return results;
}

export function createGrepTool(context: GrepContext): ToolInstance<any> {
  return Tool.define('grep', {
    description:
      'Search file contents using a regular expression within the project directory. Returns matching file paths and line numbers with context. Optionally filter by file extension.',
    parameters: z
      .object({
        pattern: z.string().describe('Regular expression pattern to search for'),
        include: z
          .string()
          .optional()
          .describe(
            'File extension filter (e.g., ".ts", ".json"). Only search files with this extension.'
          ),
      })
      .strict(),
    execute: async (params) => {
      let regex: RegExp;
      try {
        regex = new RegExp(params.pattern, 'g');
      } catch (err) {
        return `Error: invalid regex pattern: ${params.pattern}`;
      }

      const files = walkFiles(context.projectPath, params.include);
      const matches: string[] = [];
      const maxMatches = 200;
      const maxPerFile = 20;

      for (const filePath of files) {
        if (matches.length >= maxMatches) break;

        let content: string;
        try {
          content = fs.readFileSync(filePath, 'utf-8');
        } catch {
          continue;
        }

        const lines = content.split('\n');
        let fileMatchCount = 0;

        for (let i = 0; i < lines.length; i++) {
          if (matches.length >= maxMatches) break;
          if (fileMatchCount >= maxPerFile) break;

          regex.lastIndex = 0;
          if (regex.test(lines[i])) {
            const relPath = path.relative(context.projectPath, filePath);
            matches.push(`${relPath}:${i + 1}: ${lines[i]}`);
            fileMatchCount++;
          }
        }
      }

      if (matches.length === 0) {
        return 'No matches found.';
      }

      if (matches.length >= maxMatches) {
        matches.push(`(showing first ${maxMatches} matches)`);
      }

      return matches.join('\n');
    },
  });
}
