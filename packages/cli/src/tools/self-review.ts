import { z } from 'zod';
import * as fs from 'fs';
import * as path from 'path';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { runSelfReview, canGeneratePrd } from '../openspec/self-review';

export interface SelfReviewToolContext {
  projectPath: string;
}

interface PrdTask {
  id: string;
  order: number | string;
  capability: string;
  requirement_ref: string;
  title: string;
  description: string;
  acceptance_criteria: string[];
  dependencies: string[];
  estimated_effort: string;
  spec_file: string;
}

interface PrdJson {
  version: string;
  change_id: string;
  issue_reference: string;
  generated_at: string;
  tasks: PrdTask[];
  metadata: {
    total_tasks: number;
    capabilities_covered: string[];
    session_memory_path: string;
    task_status_path: string;
  };
}

function extractRequirementRefs(content: string): { ref: string; title: string }[] {
  const matches = content.match(/### Requirement: ([^\n]+)/g);
  if (!matches) return [];

  return matches.map(m => {
    const refMatch = m.match(/### Requirement: ([^\n]+)/);
    const ref = refMatch ? refMatch[1].trim() : '';
    const title = ref.replace(/[^a-zA-Z0-9]/g, '-').toLowerCase();
    return { ref, title };
  });
}

function generateTasksFromSpecs(specsPath: string): PrdTask[] {
  if (!fs.existsSync(specsPath)) {
    return [];
  }

  const tasks: PrdTask[] = [];
  let order = 1;

  try {
    const entries = fs.readdirSync(specsPath, { withFileTypes: true });
    const dirs = entries.filter(e => e.isDirectory()).map(e => e.name);

    for (const dir of dirs) {
      const specPath = path.join(specsPath, dir, 'spec.md');
      if (!fs.existsSync(specPath)) continue;

      try {
        const content = fs.readFileSync(specPath, 'utf-8');
        const requirements = extractRequirementRefs(content);

        if (requirements.length === 0) {
          tasks.push({
            id: `T-${String(order).padStart(3, '0')}`,
            order,
            capability: dir,
            requirement_ref: `REQ-${String(order).padStart(3, '0')}`,
            title: `Implement ${dir} capability`,
            description: `Implement the ${dir} capability as specified in specs/${dir}/spec.md`,
            acceptance_criteria: [],
            dependencies: [],
            estimated_effort: 'medium',
            spec_file: `specs/${dir}/spec.md`,
          });
          order++;
        } else {
          for (const req of requirements) {
            tasks.push({
              id: `T-${String(order).padStart(3, '0')}`,
              order,
              capability: dir,
              requirement_ref: req.ref,
              title: req.title,
              description: `Implement requirement ${req.ref} for ${dir}`,
              acceptance_criteria: [],
              dependencies: [],
              estimated_effort: 'medium',
              spec_file: `specs/${dir}/spec.md`,
            });
            order++;
          }
        }
      } catch {
        // Skip invalid spec files
      }
    }
  } catch {
    // Directory read failed
  }

  return tasks;
}

function generatePrd(changePath: string, issueRef: string): PrdJson | null {
  const specsPath = path.join(changePath, 'specs');
  const tasks = generateTasksFromSpecs(specsPath);

  if (tasks.length === 0) {
    return null;
  }

  return {
    version: '1.0',
    change_id: path.basename(changePath),
    issue_reference: issueRef,
    generated_at: new Date().toISOString(),
    tasks,
    metadata: {
      total_tasks: tasks.length,
      capabilities_covered: [...new Set(tasks.map(t => t.capability))],
      session_memory_path: './session-memories/',
      task_status_path: './task-status.json',
    },
  };
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createSelfReviewTool(context: SelfReviewToolContext): ToolInstance<any> {
  return Tool.define('run_self_review', {
    description:
      'Run self-review for OpenSpec Change artifacts after plan stage. ' +
      'Validates specs completeness, design feasibility, and AC coverage. ' +
      'Iterates up to 3 times, then generates prd.json if passed. ' +
      'Use this after specs are generated during plan stage.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe('Path to the Change directory (relative to project root or absolute).'),
        issue_ref: z
          .string()
          .optional()
          .describe('Issue reference for the prd.json (e.g., "Issue #42").'),
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

      const proposalPath = path.join(resolved, 'proposal.md');
      const designPath = path.join(resolved, 'design.md');
      const specsPath = path.join(resolved, 'specs');

      if (!fs.existsSync(proposalPath)) {
        return 'Error: proposal.md not found. Self-review requires proposal.md to exist.';
      }

      if (!fs.existsSync(designPath)) {
        return 'Error: design.md not found. Self-review requires design.md to exist.';
      }

      if (!fs.existsSync(specsPath)) {
        return 'Error: specs/ directory not found. Self-review requires specs/ directory to exist.';
      }

      const result = await runSelfReview({
        changePath: resolved,
        maxIterations: 3,
      });

      const lines: string[] = [];

      lines.push(`## Self-Review Result`);
      lines.push(`Change: ${params.change_path}`);
      lines.push(`Iteration: ${result.iteration}/${3}`);
      lines.push(`Status: ${result.passed ? 'PASSED' : 'FAILED'}`);
      lines.push('');

      if (result.issues.length > 0) {
        lines.push('### Issues Found:');
        for (const issue of result.issues) {
          lines.push(`- ${issue}`);
        }
        lines.push('');
      }

      if (result.fixes.length > 0) {
        lines.push('### Auto-fixes Applied:');
        for (const fix of result.fixes) {
          lines.push(`- ${fix}`);
        }
        lines.push('');
      }

      if (result.passed) {
        lines.push('All checks passed. Ready to generate prd.json.');
      } else {
        lines.push(`Reached maximum iterations (${3}) without passing all checks.`);
        lines.push('Manual intervention required. Consider:');
        lines.push('- Editing proposal.md or design.md to improve completeness');
        lines.push('- Adding missing spec files in specs/{capability}/spec.md format');
        lines.push('- Adding more detailed requirements with WHEN/THEN scenarios');
      }

      return lines.join('\n');
    },
  });
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createGeneratePrdTool(context: SelfReviewToolContext): ToolInstance<any> {
  return Tool.define('generate_prd', {
    description:
      'Generate prd.json from reviewed specs after self-review passes. ' +
      'Reads all spec files and creates structured tasks with IDs, titles, and spec references. ' +
      'Use this only after run_self_review has passed.',
    parameters: z
      .object({
        change_path: z
          .string()
          .describe('Path to the Change directory (relative to project root or absolute).'),
        issue_ref: z
          .string()
          .optional()
          .describe('Issue reference for the prd.json (e.g., "Issue #42").'),
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

      const prdPath = path.join(resolved, 'prd.json');

      if (!canGeneratePrd(resolved)) {
        return 'Error: Cannot generate prd.json. Self-review has not passed or specs are incomplete.';
      }

      const issueRef = params.issue_ref ?? `Change at ${params.change_path}`;
      const prd = generatePrd(resolved, issueRef);

      if (!prd) {
        return 'Error: No tasks could be generated from specs.';
      }

      try {
        fs.writeFileSync(prdPath, JSON.stringify(prd, null, 2), 'utf-8');
      } catch (err: unknown) {
        const message = err instanceof Error ? err.message : String(err);
        return `Error: failed to write prd.json: ${message}`;
      }

      return `prd.json generated successfully with ${prd.tasks.length} tasks at ${params.change_path}/prd.json`;
    },
  });
}
