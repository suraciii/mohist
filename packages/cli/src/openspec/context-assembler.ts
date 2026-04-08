import * as fs from 'fs';
import * as path from 'path';
import type { OpenSpecChange } from './detector';
import type { SessionLearning } from '../tools/session-memory';

export interface Task {
  id: string;
  order?: number | string;
  capability?: string;
  requirement_ref?: string;
  title: string;
  description: string;
  acceptance_criteria?: string[];
  dependencies?: string[];
  estimated_effort?: string;
  spec_file?: string;
}

export interface BuildContextOptions {
  change: OpenSpecChange;
  task: Task;
  learnings?: SessionLearning[];
  failureReason?: string;
  isRetry?: boolean;
}

export function readFileIfExists(filePath: string): string | null {
  if (fs.existsSync(filePath)) {
    try {
      return fs.readFileSync(filePath, 'utf-8');
    } catch {
      return null;
    }
  }
  return null;
}

export function loadLearningsFromDir(memoriesPath: string): SessionLearning[] {
  if (!fs.existsSync(memoriesPath)) {
    return [];
  }

  let files: string[];
  try {
    files = fs.readdirSync(memoriesPath).filter((f) => f.endsWith('.json'));
  } catch {
    return [];
  }

  const learnings: SessionLearning[] = [];
  for (const file of files) {
    const filePath = path.join(memoriesPath, file);
    try {
      const content = fs.readFileSync(filePath, 'utf-8');
      const learning = JSON.parse(content) as SessionLearning;
      learnings.push(learning);
    } catch {
      // Skip invalid files
    }
  }

  learnings.sort((a, b) => {
    const numA = parseInt(a.task_id.replace(/[^0-9]/g, ''), 10) || 0;
    const numB = parseInt(b.task_id.replace(/[^0-9]/g, ''), 10) || 0;
    return numA - numB;
  });

  return learnings;
}

export function formatLearningsForPrompt(learnings: SessionLearning[]): string {
  if (learnings.length === 0) {
    return '';
  }

  const lines: string[] = [];
  lines.push('[Previous Task Learnings]');

  for (const learning of learnings) {
    const prefix = `From ${learning.task_id}:`;
    if (!learning.success && learning.failure_reason) {
      lines.push(`${prefix} Failed: "${learning.failure_reason}"`);
      if (learning.adjustments.length > 0) {
        lines.push(`  Adjustments: ${learning.adjustments.join(', ')}`);
      }
    } else {
      lines.push(`${prefix} "${learning.execution_summary}"`);
      if (learning.insights.length > 0) {
        lines.push(`  Insights: ${learning.insights.join(', ')}`);
      }
    }
  }

  return lines.join('\n');
}

export function formatTaskForPrompt(task: Task): string {
  const lines: string[] = [];
  lines.push(`[Task ${task.id}]`);
  lines.push(`Title: ${task.title}`);
  lines.push('');
  lines.push(`Description: ${task.description}`);
  if (task.acceptance_criteria && task.acceptance_criteria.length > 0) {
    lines.push('');
    lines.push('Acceptance Criteria:');
    for (const ac of task.acceptance_criteria) {
      lines.push(`  - [ ] ${ac}`);
    }
  }
  return lines.join('\n');
}

export function formatRetryContext(failureReason: string, task: Task): string {
  const lines: string[] = [];
  lines.push('[Previous Attempt Failed]');
  lines.push(`Failure Reason: ${failureReason}`);
  lines.push('');
  lines.push('[Task]');
  lines.push(formatTaskForPrompt(task));
  return lines.join('\n');
}

export interface AssembledContext {
  proposal: string | null;
  design: string | null;
  spec: string | null;
  learnings: SessionLearning[];
  formattedLearnings: string;
  taskPrompt: string;
  fullPrompt: string;
}

export function buildTaskContext(options: BuildContextOptions): AssembledContext {
  const { change, task, learnings = [], failureReason, isRetry = false } = options;

  const proposal = readFileIfExists(change.proposalPath);
  const design = readFileIfExists(change.designPath);

  let spec: string | null = null;
  if (task.spec_file) {
    const specPath = path.join(change.changePath, task.spec_file);
    spec = readFileIfExists(specPath);
  }

  const formattedLearnings = formatLearningsForPrompt(learnings);
  const taskPrompt = formatTaskForPrompt(task);

  const sections: string[] = [];

  if (proposal) {
    sections.push('[Proposal]');
    sections.push(proposal);
    sections.push('');
  }

  if (design) {
    sections.push('[Design]');
    sections.push(design);
    sections.push('');
  }

  if (spec) {
    sections.push(`[Current Requirement: ${task.spec_file || 'spec'}]`);
    if (task.requirement_ref) {
      sections.push(`Requirement Ref: ${task.requirement_ref}`);
    }
    sections.push(spec);
    sections.push('');
  }

  if (formattedLearnings) {
    sections.push(formattedLearnings);
    sections.push('');
  }

  if (isRetry && failureReason) {
    sections.push(formatRetryContext(failureReason, task));
  } else {
    sections.push(taskPrompt);
  }

  const fullPrompt = sections.join('\n');

  return {
    proposal,
    design,
    spec,
    learnings,
    formattedLearnings,
    taskPrompt,
    fullPrompt,
  };
}

export class ContextAssembler {
  private projectPath: string;

  constructor(projectPath: string) {
    this.projectPath = projectPath;
  }

  assembleTaskContext(
    changePath: string,
    task: Task,
    options?: {
      failureReason?: string;
      isRetry?: boolean;
    }
  ): AssembledContext | null {
    const changeDir = path.resolve(this.projectPath, changePath);
    const proposalPath = path.join(changeDir, 'proposal.md');
    const designPath = path.join(changeDir, 'design.md');
    const specsPath = path.join(changeDir, 'specs');
    const sessionMemoriesPath = path.join(changeDir, 'session-memories');

    const change: OpenSpecChange = {
      changePath: changeDir,
      prdPath: path.join(changeDir, 'prd.json'),
      taskStatusPath: path.join(changeDir, 'task-status.json'),
      sessionMemoriesPath,
      proposalPath,
      designPath,
      specsPath,
    };

    const learnings = loadLearningsFromDir(sessionMemoriesPath);

    return buildTaskContext({
      change,
      task,
      learnings,
      failureReason: options?.failureReason,
      isRetry: options?.isRetry,
    });
  }
}