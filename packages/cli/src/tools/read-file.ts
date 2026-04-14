import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance, type ToolResult } from '../agent-runtime/tool';

const MAX_LINES = 2000;
const MAX_BYTES = 51200;
const MAX_LINE_LENGTH = 2000;

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
    execute: async (params): Promise<string | ToolResult> => {
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
      const allLines = content.split('\n');

      const offset = params.offset ? params.offset - 1 : 0;
      const limit = params.limit ?? allLines.length;
      const sliced = allLines.slice(offset, offset + limit);

      const lineTruncations: number[] = [];
      const processedLines = sliced.map((line, i) => {
        if (line.length > MAX_LINE_LENGTH) {
          lineTruncations.push(offset + i + 1);
          return line.slice(0, MAX_LINE_LENGTH) + '... (line truncated to 2000 chars)';
        }
        return line;
      });

      const isUserRange = params.offset !== undefined && params.limit !== undefined;
      let finalLines = processedLines;
      const hints: string[] = [];
      let wasTruncated = lineTruncations.length > 0;

      if (!isUserRange) {
        if (finalLines.length > MAX_LINES) {
          const totalInSlice = finalLines.length;
          finalLines = finalLines.slice(0, MAX_LINES);
          hints.push(
            `(Showing lines ${offset + 1}-${offset + MAX_LINES} of ${offset + totalInSlice}. Use offset=${offset + MAX_LINES + 1} to continue.)`
          );
          wasTruncated = true;
        }

        const numbered = finalLines.map((line, i) => `${offset + i + 1}: ${line}`);
        const joined = numbered.join('\n');
        if (Buffer.byteLength(joined, 'utf-8') > MAX_BYTES) {
          let byteCount = 0;
          let fitCount = 0;
          for (let i = 0; i < numbered.length; i++) {
            const sep = i > 0 ? 1 : 0;
            const lineBytes = Buffer.byteLength(numbered[i], 'utf-8') + sep;
            if (byteCount + lineBytes > MAX_BYTES && fitCount > 0) break;
            byteCount += lineBytes;
            fitCount++;
          }
          finalLines = finalLines.slice(0, fitCount);
          hints.push(
            `(Output truncated: exceeded 50KB limit at line ${offset + fitCount + 1}. Use offset=${offset + fitCount + 1} to continue.)`
          );
          wasTruncated = true;
        }
      }

      const output = finalLines
        .map((line, i) => `${offset + i + 1}: ${line}`)
        .join('\n');
      const fullOutput = hints.length > 0
        ? output + '\n\n' + hints.join('\n')
        : output;

      if (wasTruncated) {
        return {
          output: fullOutput,
          metadata: {
            truncated: true,
            lineTruncations: lineTruncations.length > 0 ? lineTruncations : undefined,
          },
        };
      }

      return fullOutput;
    },
  });
}
