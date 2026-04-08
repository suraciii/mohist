import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

export interface ReadPrdContext {
  projectPath: string;
}

interface PrdTask {
  id: string;
  order?: number;
  capability?: string;
  requirement_ref?: string;
  title: string;
  description: string;
  acceptance_criteria?: string[];
  dependencies?: string[];
  estimated_effort?: string;
  spec_file?: string;
}

interface PrdJson {
  version?: string;
  change_id?: string;
  issue_reference?: string;
  generated_at?: string;
  tasks: PrdTask[];
  metadata?: {
    total_tasks?: number;
    capabilities_covered?: string[];
    session_memory_path?: string;
    task_status_path?: string;
  };
}

function formatPrd(prd: PrdJson): string {
  const lines: string[] = [];

  lines.push(`# PRD: ${prd.change_id || 'unknown'}`);
  if (prd.issue_reference) lines.push(`Issue Reference: ${prd.issue_reference}`);
  if (prd.generated_at) lines.push(`Generated: ${prd.generated_at}`);
  lines.push('');

  lines.push(`## Tasks (${prd.tasks.length} total)`);
  lines.push('');

  for (const task of prd.tasks) {
    lines.push(`### ${task.id}: ${task.title}`);
    lines.push(`- Order: ${task.order ?? 'N/A'}`);
    if (task.capability) lines.push(`- Capability: ${task.capability}`);
    if (task.requirement_ref) lines.push(`- Requirement: ${task.requirement_ref}`);
    if (task.estimated_effort) lines.push(`- Effort: ${task.estimated_effort}`);
    if (task.dependencies && task.dependencies.length > 0) {
      lines.push(`- Dependencies: ${task.dependencies.join(', ')}`);
    }
    if (task.spec_file) {
      lines.push(`- Spec: ${task.spec_file}`);
    }
    lines.push('');
    lines.push(`Description: ${task.description}`);
    if (task.acceptance_criteria && task.acceptance_criteria.length > 0) {
      lines.push('');
      lines.push('Acceptance Criteria:');
      for (const ac of task.acceptance_criteria) {
        lines.push(`  - [ ] ${ac}`);
      }
    }
    lines.push('');
  }

  if (prd.metadata) {
    lines.push('## Metadata');
    if (prd.metadata.total_tasks != null) {
      lines.push(`- Total tasks: ${prd.metadata.total_tasks}`);
    }
    if (prd.metadata.capabilities_covered && prd.metadata.capabilities_covered.length > 0) {
      lines.push(`- Capabilities: ${prd.metadata.capabilities_covered.join(', ')}`);
    }
    if (prd.metadata.session_memory_path) {
      lines.push(`- Session memory path: ${prd.metadata.session_memory_path}`);
    }
    if (prd.metadata.task_status_path) {
      lines.push(`- Task status path: ${prd.metadata.task_status_path}`);
    }
  }

  return lines.join('\n');
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createReadPrdTool(context: ReadPrdContext): ToolInstance<any> {
  return Tool.define('read_prd', {
    description:
      'Read the prd.json file from a Change directory. Returns a structured task list with IDs, titles, descriptions, ' +
      'acceptance criteria, dependencies, and metadata. Use this to understand the full scope of work before executing tasks.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe(
            'Path to the Change directory (relative to project root or absolute). ' +
            'The directory should contain a prd.json file.',
          ),
      })
      .strict(),
    execute: async (params) => {
      const resolved = path.resolve(context.projectPath, params.change_path);

      if (
        !resolved.startsWith(context.projectPath + path.sep) &&
        resolved !== context.projectPath
      ) {
        return 'Error: path is outside the project directory';
      }

      const prdPath = path.join(resolved, 'prd.json');

      if (!fs.existsSync(prdPath)) {
        return `Error: prd.json not found at ${params.change_path}/prd.json`;
      }

      let raw: string;
      try {
        raw = fs.readFileSync(prdPath, 'utf-8');
      } catch (err: unknown) {
        const message = err instanceof Error ? err.message : String(err);
        return `Error: failed to read prd.json: ${message}`;
      }

      let prd: PrdJson;
      try {
        prd = JSON.parse(raw);
      } catch {
        return `Error: prd.json contains invalid JSON`;
      }

      if (!prd.tasks || !Array.isArray(prd.tasks)) {
        return `Error: prd.json is missing required "tasks" array`;
      }

      return formatPrd(prd);
    },
  });
}
