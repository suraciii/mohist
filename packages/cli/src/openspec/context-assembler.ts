import * as fs from 'fs';
import * as path from 'path';
import type { OpenSpecChange } from './detector';
import type { SessionLearning } from '../tools/session-memory';
import { formatAgentPrompt, type AgentPromptParts } from '../agents/agent-prompt-schema';
import { listOpenSpecContextFiles } from '../agents/workflow-context';
import type { AgentConfig } from '../workflow/workflow-loader';

const BUILD_INSTRUCTION_PATH = path.join(__dirname, '..', 'agents', 'prompts', 'build.md');

let cachedBuildInstruction: string | null = null;

function loadBuildInstruction(): string {
  if (cachedBuildInstruction !== null) return cachedBuildInstruction;
  cachedBuildInstruction = fs.readFileSync(BUILD_INSTRUCTION_PATH, 'utf-8');
  return cachedBuildInstruction;
}

export interface Task {
  id: string;
  order: number;
  title: string;
  description: string;
  acceptanceCriteria?: string[];
  dependsOn?: string[];
  spec?: string;
  passes: boolean;
  attempts: number;
  error?: string | null;
  mode?: 'AFK' | 'HITL';
  type?: 'WRITE' | 'TEST' | 'MIGRATE' | 'CONFIG' | 'REVIEW';
  output?: string;
  durations?: number[];
}

export interface BuildContextOptions {
  change: OpenSpecChange;
  task: Task;
  learnings?: SessionLearning[];
  failureReason?: string;
  isRetry?: boolean;
  wipResumeContext?: string;
  totalTasks?: number;
  issueNumber?: number;
  issueTitle?: string;
  issueBody?: string;
  agentConfig?: AgentConfig;
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
    }
  }

  learnings.sort((a, b) => {
    const numA = parseInt(a.task_id.replace(/[^0-9]/g, ''), 10) || 0;
    const numB = parseInt(b.task_id.replace(/[^0-9]/g, ''), 10) || 0;
    return numA - numB;
  });

  return learnings;
}

export function listLearningFiles(memoriesPath: string): Array<{ path: string; desc: string }> {
  if (!fs.existsSync(memoriesPath)) {
    return [];
  }

  let files: string[];
  try {
    files = fs.readdirSync(memoriesPath).filter((f) => f.endsWith('.json')).sort();
  } catch {
    return [];
  }

  return files.map((f) => ({
    path: path.join(memoriesPath, f),
    desc: `Previous task learning from ${path.basename(f, '.json')}`,
  }));
}

export function formatTaskBlock(task: Task): string {
  const lines: string[] = [];
  lines.push(`ID: ${task.id}`);
  lines.push(`Title: ${task.title}`);
  if (task.mode) {
    lines.push(`Mode: ${task.mode}`);
  }
  if (task.type) {
    lines.push(`Type: ${task.type}`);
  }
  if (task.output) {
    lines.push(`Output: ${task.output}`);
  }
  if (task.dependsOn && task.dependsOn.length > 0) {
    lines.push(`Depends On: ${task.dependsOn.join(', ')}`);
  }
  lines.push('');
  lines.push(task.description);
  if (task.acceptanceCriteria && task.acceptanceCriteria.length > 0) {
    lines.push('');
    lines.push('Acceptance Criteria:');
    for (const ac of task.acceptanceCriteria) {
      lines.push(`- [ ] ${ac}`);
    }
  }
  return lines.join('\n');
}

export interface AssembledContext {
  proposal: string | null;
  design: string | null;
  spec: string | null;
  learnings: SessionLearning[];
  fullPrompt: string;
}

function buildRole(task: Task, options: BuildContextOptions): string {
  const parts: string[] = ['You are implementing task'];
  if (options.totalTasks) {
    parts.push(`${task.id} of ${options.totalTasks}`);
  } else {
    parts.push(task.id);
  }
  if (options.issueNumber) {
    parts.push(`for issue #${options.issueNumber}`);
  }
  return parts.join(' ');
}

function buildContract(task: Task): string {
  const lines: string[] = [];
  lines.push('After completing this task, stage and commit your changes.');
  lines.push(`Commit message must start with "${task.id}: " followed by a brief summary.`);
  return lines.join('\n');
}

function buildTaskContent(task: Task, options: BuildContextOptions): string {
  const parts: string[] = [];

  if (options.issueNumber || options.issueTitle || options.issueBody) {
    parts.push(`Issue #${options.issueNumber ?? 'unknown'}: ${options.issueTitle ?? task.title}`);
    if (options.issueBody) {
      parts.push('');
      parts.push(options.issueBody);
    }
    parts.push('');
  }

  if (options.isRetry && options.failureReason) {
    parts.push('[Previous Attempt Failed]');
    parts.push(`Failure Reason: ${options.failureReason}`);
    parts.push('');
  }

  parts.push(formatTaskBlock(task));

  if (options.wipResumeContext) {
    parts.push('');
    parts.push('[WIP Resume]');
    parts.push(options.wipResumeContext);
  }

  if (options.totalTasks) {
    const completedBefore = (task.order || parseInt(task.id.replace(/[^0-9]/g, ''), 10) || 1) - 1;
    parts.push('');
    parts.push(`Progress: ${completedBefore} of ${options.totalTasks} tasks completed before this one.`);
  }

  return parts.join('\n');
}

export function buildTaskContext(options: BuildContextOptions): AssembledContext {
  const { change, task, learnings = [] } = options;

  const proposal = readFileIfExists(change.proposalPath);
  const design = readFileIfExists(change.designPath);

  let spec: string | null = null;
  if (task.spec) {
    const specPath = path.join(change.changePath, task.spec);
    spec = readFileIfExists(specPath);
  }

  const contextFiles = listOpenSpecContextFiles(change.changePath, { includeReports: true });
  const learningFiles = listLearningFiles(change.sessionMemoriesPath);
  contextFiles.push(...learningFiles);

  const role = buildRole(task, options);
  const taskContent = buildTaskContent(task, options);
  const contract = buildContract(task);

  const parts: AgentPromptParts = {
    role,
    projectContext: options.agentConfig?.context,
    rules: options.agentConfig?.rules?.build,
    contextFiles: contextFiles.length > 0 ? contextFiles : undefined,
    spec: spec ?? undefined,
    task: taskContent,
    contract,
    instruction: loadBuildInstruction(),
  };

  const fullPrompt = formatAgentPrompt(parts);

  return {
    proposal,
    design,
    spec,
    learnings,
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
      wipResumeContext?: string;
      totalTasks?: number;
      issueNumber?: number;
      agentConfig?: AgentConfig;
    }
  ): AssembledContext | null {
    const changeDir = path.resolve(this.projectPath, changePath);
    const proposalPath = path.join(changeDir, 'proposal.md');
    const designPath = path.join(changeDir, 'design.md');
    const specsPath = path.join(changeDir, 'specs');
    const sessionMemoriesPath = path.join(changeDir, 'session-memories');

    const change: OpenSpecChange = {
      changePath: changeDir,
      tasksPath: path.join(changeDir, 'tasks.json'),
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
      wipResumeContext: options?.wipResumeContext,
      totalTasks: options?.totalTasks,
      issueNumber: options?.issueNumber,
      agentConfig: options?.agentConfig,
    });
  }
}
