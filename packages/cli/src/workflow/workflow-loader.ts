import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';
import { findChangeDir } from '../openspec/detector';
import { load as loadConfig, getAgentTimeoutConfig } from '../config/config-loader';
import { Log } from '../util/log';
import { Stage } from '../types';

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
  healthGates?: unknown;
  checks?: unknown;
}

export interface OpenSpecDetection {
  detected: boolean;
  changePath?: string;
  tasksPath?: string;
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
        '为 issue #{issue.number}: {issue.title} 制定实现计划',
      approval: true,
    },
    {
      stage: 'build',
      prompt: '实现 {issue.title}，按 plan 阶段的计划进行',
      approval: false,
    },
    {
      stage: 'check',
      prompt: '审查实现成果，检查功能正确性和代码质量',
      approval: true,
    },
    {
      stage: 'integrate',
      prompt: '将实现成果合并到主分支，验证完整性',
      approval: true,
    },
    {
      stage: 'done',
      prompt: '标记 issue #{issue.number} 为已完成',
      approval: false,
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
      healthGates: parsed.healthGates,
      checks: parsed.checks,
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

  let workflow: WorkflowConfig | undefined;

  for (const candidate of candidates) {
    const result = parseWorkflowFile(candidate);
    if (result === 'ENOENT') continue;
    if (typeof result === 'string') {
      log.warn('Workflow file error, falling back to default', { error: result });
      workflow = DEFAULT_WORKFLOW;
      break;
    }
    workflow = result;
    break;
  }

  if (!workflow) {
    workflow = DEFAULT_WORKFLOW;
  }

  const { stageTimeout } = getAgentTimeoutConfig(loadConfig());
  workflow.stages = workflow.stages.map((s) => ({
    ...s,
    timeout: s.timeout ?? stageTimeout,
  }));

  return workflow;
}

export function detectOpenSpecForIssue(cwd: string, issueNumber: number): OpenSpecDetection {
  const changePath = findChangeDir(cwd, issueNumber);

  if (!changePath) {
    return { detected: false, mode: 'traditional' };
  }

  const tasksPath = path.join(changePath, 'tasks.json');

  if (!fs.existsSync(tasksPath)) {
    return { detected: true, changePath, mode: 'traditional' };
  }

  return { detected: true, changePath, tasksPath, mode: 'openspec' };
}

export type HealthGateStage = 'plan' | 'build' | 'check' | 'postMerge';

export interface HealthGatePolicy {
  enabled: boolean;
  command: string;
  timeout: number;
  autoFix: boolean;
  maxFixAttempts: number;
  fallbackReaction: ReactionConfig;
}

export interface HealthGatePolicies {
  plan: HealthGatePolicy;
  build: HealthGatePolicy;
  check: HealthGatePolicy;
  postMerge: HealthGatePolicy;
}

export interface ReactionConfig {
  type: 'retry-task' | 'auto-fix' | 'escalate' | 'ask-user';
  maxAttempts?: number;
  escalateTarget?: Stage;
  fallbackReaction?: ReactionConfig;
}

const DEFAULT_PLAN_COMMAND = 'npm run typecheck';
const DEFAULT_BUILD_COMMAND = 'npm run build';
const DEFAULT_CHECK_COMMAND = 'npm run build && npm test';

export const DEFAULT_HEALTH_GATE_POLICIES: HealthGatePolicies = {
  plan: {
    enabled: true,
    command: DEFAULT_PLAN_COMMAND,
    timeout: 5 * 60 * 1000,
    autoFix: false,
    maxFixAttempts: 0,
    fallbackReaction: { type: 'ask-user' },
  },
  build: {
    enabled: true,
    command: DEFAULT_BUILD_COMMAND,
    timeout: 5 * 60 * 1000,
    autoFix: true,
    maxFixAttempts: 2,
    fallbackReaction: { type: 'escalate', escalateTarget: Stage.Plan },
  },
  check: {
    enabled: true,
    command: DEFAULT_CHECK_COMMAND,
    timeout: 5 * 60 * 1000,
    autoFix: true,
    maxFixAttempts: 2,
    fallbackReaction: { type: 'escalate', escalateTarget: Stage.Build },
  },
  postMerge: {
    enabled: true,
    command: DEFAULT_CHECK_COMMAND,
    timeout: 5 * 60 * 1000,
    autoFix: false,
    maxFixAttempts: 0,
    fallbackReaction: { type: 'ask-user' },
  },
};

function parseHealthGatePolicy(
  raw: unknown,
  defaults: HealthGatePolicy,
  checksBuildTest?: BuildTestCheckConfig,
): HealthGatePolicy {
  if (!raw || typeof raw !== 'object') {
    return checksBuildTest
      ? {
          enabled: defaults.enabled,
          command: checksBuildTest.command,
          timeout: checksBuildTest.timeout,
          autoFix: checksBuildTest.autoFix,
          maxFixAttempts: checksBuildTest.maxFixAttempts,
          fallbackReaction: defaults.fallbackReaction,
        }
      : defaults;
  }

  const r = raw as Record<string, unknown>;

  const command = (typeof r.command === 'string' && r.command.length > 0)
    ? r.command
    : (checksBuildTest ? checksBuildTest.command : defaults.command);

  const timeout = typeof r.timeout === 'number' ? r.timeout : defaults.timeout;
  const autoFix = typeof r.autoFix === 'boolean' ? r.autoFix : defaults.autoFix;
  const maxFixAttempts = typeof r.maxFixAttempts === 'number' ? r.maxFixAttempts : defaults.maxFixAttempts;

  let enabled = defaults.enabled;
  if (r.enabled !== undefined) {
    enabled = r.enabled === true;
  }

  let fallbackReaction = defaults.fallbackReaction;
  if (r.fallbackReaction && typeof r.fallbackReaction === 'object') {
    const fr = r.fallbackReaction as Record<string, unknown>;
    fallbackReaction = {
      type: typeof fr.type === 'string' ? fr.type as ReactionConfig['type'] : defaults.fallbackReaction.type,
      maxAttempts: typeof fr.maxAttempts === 'number' ? fr.maxAttempts : defaults.fallbackReaction.maxAttempts,
      escalateTarget: typeof fr.escalateTarget === 'string' ? fr.escalateTarget as Stage : defaults.fallbackReaction.escalateTarget,
    };
  }

  return { enabled, command, timeout, autoFix, maxFixAttempts, fallbackReaction };
}

export function loadHealthGatePolicies(workflow: WorkflowConfig): HealthGatePolicies {
  const parsed = workflow as any;
  const healthGates = parsed.healthGates;
  const checks = parsed.checks as any;
  const checksBuildTest: BuildTestCheckConfig | undefined = checks?.buildTest
    ? {
        command: typeof checks.buildTest.command === 'string' ? checks.buildTest.command : DEFAULT_CHECKS_CONFIG.buildTest.command,
        timeout: typeof checks.buildTest.timeout === 'number' ? checks.buildTest.timeout : DEFAULT_CHECKS_CONFIG.buildTest.timeout,
        autoFix: typeof checks.buildTest.autoFix === 'boolean' ? checks.buildTest.autoFix : DEFAULT_CHECKS_CONFIG.buildTest.autoFix,
        maxFixAttempts: typeof checks.buildTest.maxFixAttempts === 'number' ? checks.buildTest.maxFixAttempts : DEFAULT_CHECKS_CONFIG.buildTest.maxFixAttempts,
      }
    : undefined;

  const hasExplicitCheckGate = healthGates && typeof healthGates === 'object' && healthGates.check !== undefined;

  return {
    plan: parseHealthGatePolicy(
      healthGates?.plan,
      DEFAULT_HEALTH_GATE_POLICIES.plan,
    ),
    build: parseHealthGatePolicy(
      healthGates?.build,
      DEFAULT_HEALTH_GATE_POLICIES.build,
    ),
    check: parseHealthGatePolicy(
      healthGates?.check,
      DEFAULT_HEALTH_GATE_POLICIES.check,
      hasExplicitCheckGate ? undefined : checksBuildTest,
    ),
    postMerge: parseHealthGatePolicy(
      healthGates?.postMerge,
      DEFAULT_HEALTH_GATE_POLICIES.postMerge,
    ),
  };
}

export interface BuildTestCheckConfig {
  command: string;
  timeout: number;
  autoFix: boolean;
  maxFixAttempts: number;
}

export interface FfMergeCheckConfig {
  enabled: boolean;
}

export interface AiReviewCheckConfig {
  enabled: boolean;
}

export interface ChecksConfig {
  buildTest: BuildTestCheckConfig;
  ffMerge: FfMergeCheckConfig;
  aiReview: AiReviewCheckConfig;
}

export const DEFAULT_CHECKS_CONFIG: ChecksConfig = {
  buildTest: {
    command: 'npm run build && npm test',
    timeout: 5 * 60 * 1000,
    autoFix: true,
    maxFixAttempts: 2,
  },
  ffMerge: {
    enabled: true,
  },
  aiReview: {
    enabled: true,
  },
};

function parseChecksConfig(raw: unknown): ChecksConfig | undefined {
  if (!raw || typeof raw !== 'object') return undefined;
  const r = raw as Record<string, unknown>;

  let buildTest: BuildTestCheckConfig | undefined;
  if (r.buildTest && typeof r.buildTest === 'object') {
    const bt = r.buildTest as Record<string, unknown>;
    buildTest = {
      command: typeof bt.command === 'string' ? bt.command : DEFAULT_CHECKS_CONFIG.buildTest.command,
      timeout: typeof bt.timeout === 'number' ? bt.timeout : DEFAULT_CHECKS_CONFIG.buildTest.timeout,
      autoFix: typeof bt.autoFix === 'boolean' ? bt.autoFix : DEFAULT_CHECKS_CONFIG.buildTest.autoFix,
      maxFixAttempts: typeof bt.maxFixAttempts === 'number' ? bt.maxFixAttempts : DEFAULT_CHECKS_CONFIG.buildTest.maxFixAttempts,
    };
  }

  let ffMerge: FfMergeCheckConfig | undefined;
  if (r.ffMerge && typeof r.ffMerge === 'object') {
    const ff = r.ffMerge as Record<string, unknown>;
    ffMerge = {
      enabled: typeof ff.enabled === 'boolean' ? ff.enabled : DEFAULT_CHECKS_CONFIG.ffMerge.enabled,
    };
  }

  let aiReview: AiReviewCheckConfig | undefined;
  if (r.aiReview && typeof r.aiReview === 'object') {
    const ar = r.aiReview as Record<string, unknown>;
    aiReview = {
      enabled: typeof ar.enabled === 'boolean' ? ar.enabled : DEFAULT_CHECKS_CONFIG.aiReview.enabled,
    };
  }

  return {
    buildTest: buildTest ?? DEFAULT_CHECKS_CONFIG.buildTest,
    ffMerge: ffMerge ?? DEFAULT_CHECKS_CONFIG.ffMerge,
    aiReview: aiReview ?? DEFAULT_CHECKS_CONFIG.aiReview,
  };
}

export function loadChecksConfig(workflow: WorkflowConfig): ChecksConfig {
  const parsed = workflow as any;
  if (!parsed.checks) return DEFAULT_CHECKS_CONFIG;
  return parseChecksConfig(parsed.checks) ?? DEFAULT_CHECKS_CONFIG;
}

export interface AgentConfig {
  context?: string;
  rules?: Record<string, string[]>;
}

export function loadAgentConfig(cwd: string): AgentConfig {
  const candidates = [
    path.join(cwd, 'workflow.yaml'),
    path.join(cwd, '.mohist', 'workflow.yaml'),
  ];

  for (const candidate of candidates) {
    try {
      const content = fs.readFileSync(candidate, 'utf-8');
      const parsed = yaml.parse(content);
      if (!parsed || typeof parsed !== 'object') continue;
      if (!parsed.agent || typeof parsed.agent !== 'object') continue;

      const agent = parsed.agent as Record<string, unknown>;
      const result: AgentConfig = {};

      if (typeof agent.context === 'string' && agent.context.length > 0) {
        result.context = agent.context;
      }

      if (agent.rules && typeof agent.rules === 'object') {
        const rules: Record<string, string[]> = {};
        for (const [stage, val] of Object.entries(agent.rules as Record<string, unknown>)) {
          if (Array.isArray(val) && val.every((v) => typeof v === 'string')) {
            rules[stage] = val as string[];
          }
        }
        if (Object.keys(rules).length > 0) {
          result.rules = rules;
        }
      }

      return result;
    } catch {
      continue;
    }
  }

  return {};
}

export function loadWorkflowWithDetection(cwd: string, issueNumber: number): WorkflowConfigWithDetection | string {
  const workflow = loadWorkflow(cwd);
  if (typeof workflow === 'string') return workflow;

  const openspec = detectOpenSpecForIssue(cwd, issueNumber);
  return { ...workflow, openspec };
}
