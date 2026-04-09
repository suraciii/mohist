import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

export interface WriteFileContext {
  projectPath: string;
}

export function createWriteFileTool(context: WriteFileContext): ToolInstance<any> {
  return Tool.define('write_file', {
    description:
      'Write content to a file. Creates parent directories if they do not exist. Use this to create new files or overwrite existing files.',
    parameters: z
      .object({
        path: z.string().describe('File path relative to the project root'),
        content: z.string().describe('Content to write to the file'),
        append: z
          .boolean()
          .optional()
          .default(false)
          .describe('If true, append to existing file instead of overwriting'),
      })
      .strict(),
    execute: async (params) => {
      try {
        const resolved = path.resolve(context.projectPath, params.path);

        // Security check: ensure path is within project directory
        if (!resolved.startsWith(context.projectPath + path.sep) && resolved !== context.projectPath) {
          return `Error: Path is outside the project directory`;
        }

        // Create parent directories if they don't exist
        const parentDir = path.dirname(resolved);
        if (!fs.existsSync(parentDir)) {
          fs.mkdirSync(parentDir, { recursive: true });
        }

        // Write file
        if (params.append && fs.existsSync(resolved)) {
          fs.appendFileSync(resolved, params.content, 'utf-8');
        } else {
          fs.writeFileSync(resolved, params.content, 'utf-8');
        }

        const bytesWritten = Buffer.byteLength(params.content, 'utf-8');
        return `Successfully wrote ${bytesWritten} bytes to ${params.path}`;
      } catch (error) {
        return `Error: ${error instanceof Error ? error.message : 'Unknown error writing file'}`;
      }
    },
  });
}
