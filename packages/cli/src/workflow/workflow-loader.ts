import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';
import { findChangeDir } from '../openspec/detector';

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
      stage: 'plan',
      prompt:
        '分析 issue #{issue.number}: {issue.title}，探索 codebase，产出实现计划',
      approval: false,
      timeout: 600,
    },
    {
      stage: 'build',
      prompt:
        '按 plan 阶段的计划实现 {issue.title}。计划摘要：{plan.output}',
      approval: true,
      timeout: 1800,
    },
    {
      stage: 'check',
      prompt:
        '检查 {issue.title} 的实现：运行测试、lint、typecheck，报告问题',
      approval: true,
      timeout: 600,
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
      console.error(
        `[workflow-loader] ${result}. Falling back to default workflow.`
      );
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
