import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

export interface ReadFileContext {
  projectPath: string;
}

export function createReadFileTool(context: ReadFileContext): ToolInstance<any> {
  return Tool.define('read_file', {
    description:
      'Read file content from the project directory. Supports optional line range (offset and limit) to read specific portions of large files.',
    parameters: z
      .object({
        path: z.string().describe('File path relative to the project root'),
        offset: z
          .number()
          .int()
          .positive()
          .optional()
          .describe('1-indexed line number to start reading from'),
        limit: z
          .number()
          .int()
          .positive()
          .optional()
          .describe('Maximum number of lines to read'),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.path);

      if (!resolved.startsWith(context.projectPath + path.sep) && resolved !== context.projectPath) {
        return 'Error: path is outside the project directory';
      }

      if (!fs.existsSync(resolved)) {
        return `Error: file not found: ${params.path}`;
      }

      const stat = fs.statSync(resolved);
      if (!stat.isFile()) {
        return `Error: not a file: ${params.path}`;
      }

      const content = fs.readFileSync(resolved, 'utf-8');
      const lines = content.split('\n');

      const offset = params.offset ? params.offset - 1 : 0;
      const limit = params.limit ?? lines.length;
      const sliced = lines.slice(offset, offset + limit);

      return sliced
        .map((line, i) => `${offset + i + 1}: ${line}`)
        .join('\n');
    },
  });
}
