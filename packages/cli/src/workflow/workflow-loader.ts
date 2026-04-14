import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';
import { findChangeDir } from '../openspec/detector';
import { Log } from '../util/log';

const log = Log.create({ service: 'workflow' });

export interface WorkflowStage {
  stage: string;
  prompt: string;
  approval?: boolean;
  timeout?: number;
}

export interface WorkflowConfig {
  stages: WorkflowStage[];
  source: string;
}

export interface OpenSpecDetection {
  detected: boolean;
  changePath?: string;
  prdPath?: string;
  mode: 'openspec' | 'traditional';
}

export interface WorkflowConfigWithDetection extends WorkflowConfig {
  openspec: OpenSpecDetection;
}

const DEFAULT_WORKFLOW: WorkflowConfig = {
  stages: [
    {
      stage: 'explore',
      prompt:
        '探索 issue #{issue.number}: {issue.title}，分析问题背景和 codebase',
      approval: false,
      timeout: 600,
    },
    {
      stage: 'plan',
      prompt:
        '基于探索结果，为 issue #{issue.number}: {issue.title} 制定实现计划',
      approval: true,
      timeout: 600,
    },
    {
      stage: 'build',
      prompt: '实现 {issue.title}，按 plan 阶段的计划进行',
      approval: false,
      timeout: 1800,
    },
    {
      stage: 'review',
      prompt: '审查实现成果，检查功能正确性和代码质量',
      approval: true,
      timeout: 600,
    },
    {
      stage: 'done',
      prompt: '标记 issue #{issue.number} 为已完成',
      approval: false,
      timeout: 300,
    },
  ],
  source: 'builtin',
};

function parseWorkflowFile(filePath: string): WorkflowConfig | string {
  try {
    const content = fs.readFileSync(filePath, 'utf-8');
    const parsed = yaml.parse(content);

    if (!parsed || typeof parsed !== 'object' || !Array.isArray(parsed.stages)) {
      return `Error: ${filePath} is missing a valid "stages" array`;
    }

    for (const s of parsed.stages) {
      if (!s.stage || typeof s.stage !== 'string') {
        return `Error: each stage must have a "stage" string field`;
      }
    }

    return {
      stages: parsed.stages.map((s: Record<string, unknown>) => ({
        stage: String(s.stage),
        prompt: String(s.prompt ?? ''),
        approval: Boolean(s.approval),
        timeout: typeof s.timeout === 'number' ? s.timeout : undefined,
      })),
      source: filePath,
    };
  } catch (err) {
    if (err instanceof yaml.YAMLParseError) {
      return `Error: failed to parse ${filePath}: ${err.message}`;
    }
    if ((err as NodeJS.ErrnoException).code === 'ENOENT') {
      return 'ENOENT';
    }
    return `Error: failed to read ${filePath}: ${err instanceof Error ? err.message : String(err)}`;
  }
}

export function loadWorkflow(cwd: string): WorkflowConfig | string {
  const candidates = [
    path.join(cwd, 'workflow.yaml'),
    path.join(cwd, '.mohist', 'workflow.yaml'),
  ];

  for (const candidate of candidates) {
    const result = parseWorkflowFile(candidate);
    if (result === 'ENOENT') continue;
    if (typeof result === 'string') {
      log.warn('Workflow file error, falling back to default', { error: result });
      return DEFAULT_WORKFLOW;
    }
    return result;
  }

  return DEFAULT_WORKFLOW;
}

export function detectOpenSpecForIssue(cwd: string, issueNumber: number): OpenSpecDetection {
  const changePath = findChangeDir(cwd, issueNumber);

  if (!changePath) {
    return { detected: false, mode: 'traditional' };
  }

  const prdPath = path.join(changePath, 'prd.json');

  if (!fs.existsSync(prdPath)) {
    return { detected: true, changePath, mode: 'traditional' };
  }

  return { detected: true, changePath, prdPath, mode: 'openspec' };
}

export function loadWorkflowWithDetection(cwd: string, issueNumber: number): WorkflowConfigWithDetection | string {
  const workflow = loadWorkflow(cwd);
  if (typeof workflow === 'string') return workflow;

  const openspec = detectOpenSpecForIssue(cwd, issueNumber);
  return { ...workflow, openspec };
}
