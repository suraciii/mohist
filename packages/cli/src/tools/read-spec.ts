import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

export interface ReadSpecContext {
  projectPath: string;
}

function extractRequirement(content: string, requirementRef: string): string {
  const escapedRef = requirementRef.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const headerPattern = new RegExp(
    `^(#{2,4})\\s+(?:Requirement:\\s*)?${escapedRef}[\\s\\S]*?(?=^\\1\\s+Requirement:|$(?!\\n))`,
    'm'
  );
  const match = content.match(headerPattern);
  if (match) {
    return match[0].trim();
  }

  const sectionPattern = new RegExp(
    `^#{2,4}\\s+.*${escapedRef}[^\\n]*\\n(?:[ \\t]*[^#\\n][^\\n]*\\n|\\n)*`,
    'm'
  );
  const sectionMatch = content.match(sectionPattern);
  if (sectionMatch) {
    return sectionMatch[0].trim();
  }

  return content;
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createReadSpecTool(context: ReadSpecContext): ToolInstance<any> {
  return Tool.define('read_spec', {
    description:
      'Read a spec.md file from a Change directory. Returns the spec content with metadata. ' +
      'Optionally filter by a requirement reference (e.g. "REQ-001" or a requirement title) to get only the matching section.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe(
            'Path to the Change directory (relative to project root or absolute).',
          ),
        spec_path: z
          .string()
          .describe(
            'Path to the spec file relative to the Change directory (e.g. "specs/session-memory/spec.md").',
          ),
        requirement_ref: z
          .string()
          .optional()
          .describe(
            'Optional requirement reference to filter (e.g. "REQ-001" or a requirement title like "Session memory storage"). ' +
            'If omitted, returns the full spec content.',
          ),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.change_path);

      if (
        !resolved.startsWith(context.projectPath + path.sep) &&
        resolved !== context.projectPath
      ) {
        return 'Error: change_path is outside the project directory';
      }

      const specPath = path.join(resolved, params.spec_path);

      if (
        !specPath.startsWith(resolved + path.sep) &&
        specPath !== resolved
      ) {
        return 'Error: spec_path escapes the change directory';
      }

      if (!fs.existsSync(specPath)) {
        return `Error: spec file not found at ${params.change_path}/${params.spec_path}`;
      }

      const stat = fs.statSync(specPath);
      if (!stat.isFile()) {
        return `Error: not a file: ${params.spec_path}`;
      }

      let content: string;
      try {
        content = fs.readFileSync(specPath, 'utf-8');
      } catch (err: unknown) {
        const message = err instanceof Error ? err.message : String(err);
        return `Error: failed to read spec file: ${message}`;
      }

      const header = `# Spec: ${params.spec_path}`;
      const meta = `- File: ${params.change_path}/${params.spec_path}`;
      const metaLineCount = content.match(/### Requirement:/g);
      const reqCount = metaLineCount ? metaLineCount.length : 0;
      const metaLine = `- Requirements: ${reqCount}`;

      if (params.requirement_ref) {
        const section = extractRequirement(content, params.requirement_ref);
        if (section === content) {
          return [
            header,
            meta,
            metaLine,
            '',
            `Note: requirement "${params.requirement_ref}" not found, returning full spec.`,
            '',
            '---',
            '',
            content,
          ].join('\n');
        }
        return [header, meta, metaLine, '', `Filtered by: ${params.requirement_ref}`, '', '---', '', section].join('\n');
      }

      return [header, meta, metaLine, '', '---', '', content].join('\n');
    },
  });
}
