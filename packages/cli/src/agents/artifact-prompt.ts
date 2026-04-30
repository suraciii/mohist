import * as fs from 'fs';
import * as path from 'path';
import type { Issue } from '../types';
import type { AgentConfig } from '../workflow/workflow-loader';
import { formatAgentPrompt } from './agent-prompt-schema';

export type ArtifactType = 'proposal' | 'specs' | 'design' | 'tasks';

const ARTIFACTS_DIR = path.join(__dirname, 'prompts', 'artifacts');
const TEMPLATES_DIR = path.join(ARTIFACTS_DIR, 'templates');
const REVIEW_PROMPT_PATH = path.join(__dirname, 'prompts', 'review.md');
const REVIEW_SELF_CHECK_PATH = path.join(ARTIFACTS_DIR, 'review-self-check.md');
const EXPLORE_PROMPT_PATH = path.join(__dirname, 'prompts', 'explore.md');
const CONFLICT_RESOLUTION_PROMPT_PATH = path.join(__dirname, 'prompts', 'conflict-resolution.md');
const RE_VERIFY_PROMPT_PATH = path.join(ARTIFACTS_DIR, 're-verify.md');
const AUTO_FIX_PROMPT_PATH = path.join(__dirname, 'prompts', 'auto-fix.md');

const ARTIFACT_OUTPUT_FILES: Record<ArtifactType, string> = {
  proposal: 'proposal.md',
  specs: 'specs/',
  design: 'design.md',
  tasks: 'tasks.json',
};

const ARTIFACT_DESCRIPTIONS: Record<ArtifactType, string> = {
  proposal: 'Create the proposal document that establishes WHY this change is needed.',
  specs: 'Create specification files that define WHAT the system should do.',
  design: 'Create the design document that explains HOW to implement the change.',
  tasks: 'Create the tasks.json file that defines implementation tasks for autonomous execution.',
};

const DEPENDENCY_ORDER: ArtifactType[] = ['proposal', 'specs', 'design', 'tasks'];

const fileCache = new Map<string, string>();

function loadFile(filePath: string): string {
  const cached = fileCache.get(filePath);
  if (cached) return cached;

  if (!fs.existsSync(filePath)) {
    throw new Error(`File not found: ${filePath}`);
  }

  const content = fs.readFileSync(filePath, 'utf-8');
  fileCache.set(filePath, content);
  return content;
}

function loadSpecContext(changeDir: string): string {
  const parts: string[] = [];
  const specsDir = path.join(changeDir, 'specs');

  if (fs.existsSync(specsDir) && fs.statSync(specsDir).isDirectory()) {
    const mdFiles: string[] = [];
    const entries = fs.readdirSync(specsDir, { recursive: true, encoding: 'utf-8' });
    for (const entry of entries) {
      if (typeof entry === 'string' && entry.endsWith('.md')) {
        mdFiles.push(entry);
      }
    }
    mdFiles.sort();
    if (mdFiles.length > 0) {
      const sections: string[] = ['## Specs'];
      for (const relPath of mdFiles) {
        const fullPath = path.join(specsDir, relPath);
        const content = fs.readFileSync(fullPath, 'utf-8');
        sections.push(`### ${relPath}\n\n${content}`);
      }
      parts.push(sections.join('\n\n'));
    }
  }

  return parts.join('\n\n');
}

function formatIssueInfo(issue: Issue): string {
  let info = `Issue #${issue.number}: ${issue.title}`;
  if (issue.body) {
    info += `\n\n${issue.body}`;
  }
  return info;
}

function buildDependencyFiles(artifactType: ArtifactType, changeDir: string): Array<{ path: string; desc: string }> {
  const myIndex = DEPENDENCY_ORDER.indexOf(artifactType);
  const files: Array<{ path: string; desc: string }> = [];

  for (let i = 0; i < myIndex; i++) {
    const depType = DEPENDENCY_ORDER[i];
    const depFile = ARTIFACT_OUTPUT_FILES[depType];
    const depPath = path.join(changeDir, depFile);
    if (fs.existsSync(depPath)) {
      files.push({ path: depPath, desc: `Previous artifact: ${depType}` });
    }
  }

  return files;
}

export function buildArtifactPrompt(
  artifactType: ArtifactType,
  issue: Issue,
  changeDir: string,
  agentConfig?: AgentConfig,
): string {
  const instructionFile = path.join(ARTIFACTS_DIR, `${artifactType}.md`);
  const instruction = loadFile(instructionFile);

  const templateFile = path.join(TEMPLATES_DIR, `${artifactType}.tpl.md`);
  const template = loadFile(templateFile);

  const outputFile = ARTIFACT_OUTPUT_FILES[artifactType];
  const outputPath = path.join(changeDir, outputFile);
  const depFiles = buildDependencyFiles(artifactType, changeDir);

  const taskContent = [
    `Create the ${artifactType} artifact for this change.`,
    ARTIFACT_DESCRIPTIONS[artifactType],
    '',
    formatIssueInfo(issue),
  ].join('\n');

  return formatAgentPrompt({
    role: `Create the ${artifactType} artifact for this change`,
    projectContext: agentConfig?.context,
    rules: agentConfig?.rules?.plan,
    contextFiles: depFiles.length > 0 ? depFiles : undefined,
    task: taskContent,
    contract: `Write the output file to: ${outputPath}`,
    template,
    instruction,
  });
}

function listExistingArtifacts(changeDir: string): Array<{ path: string; desc: string }> {
  const artifacts: Array<{ path: string; desc: string }> = [];
  const proposalPath = path.join(changeDir, 'proposal.md');
  const designPath = path.join(changeDir, 'design.md');
  const tasksPath = path.join(changeDir, 'tasks.json');
  const specsDir = path.join(changeDir, 'specs');

  if (fs.existsSync(proposalPath)) {
    artifacts.push({ path: proposalPath, desc: 'Proposal' });
  }
  if (fs.existsSync(designPath)) {
    artifacts.push({ path: designPath, desc: 'Design' });
  }
  if (fs.existsSync(tasksPath)) {
    artifacts.push({ path: tasksPath, desc: 'Tasks' });
  }
  if (fs.existsSync(specsDir) && fs.statSync(specsDir).isDirectory()) {
    artifacts.push({ path: specsDir, desc: 'Specs directory' });
  }

  return artifacts;
}

export function buildSelfReviewPrompt(
  issue: Issue,
  changeDir: string,
  agentConfig?: AgentConfig,
): string {
  const instructionFile = path.join(ARTIFACTS_DIR, 'self-review.md');
  const instruction = loadFile(instructionFile);
  const existingArtifacts = listExistingArtifacts(changeDir);

  const taskContent = [
    `Change Directory: ${changeDir}`,
    '',
    formatIssueInfo(issue),
    '',
    'Self-review all generated artifacts.',
  ].join('\n');

  return formatAgentPrompt({
    role: 'Self-review all generated artifacts for this change',
    projectContext: agentConfig?.context,
    rules: agentConfig?.rules?.plan,
    contextFiles: existingArtifacts.length > 0 ? existingArtifacts : undefined,
    task: taskContent,
    instruction,
  });
}

export function buildReviewerPrompt(
  issue: Issue,
  changeDir: string,
  agentConfig?: AgentConfig,
): string {
  const instruction = loadFile(REVIEW_PROMPT_PATH);
  const specContext = loadSpecContext(changeDir);

  const taskContent = [
    `Change Directory: ${changeDir}`,
    '',
    formatIssueInfo(issue),
    '',
    'Review the implementation for quality.',
  ].join('\n');

  return formatAgentPrompt({
    role: 'Review the implementation for quality',
    projectContext: agentConfig?.context,
    rules: agentConfig?.rules?.review,
    spec: specContext || undefined,
    task: taskContent,
    instruction,
  });
}

export function buildReviewSelfCheckPrompt(
  issue: Issue,
  changeDir: string,
  agentConfig?: AgentConfig,
): string {
  const instruction = loadFile(REVIEW_SELF_CHECK_PATH);

  const taskContent = [
    `Change Directory: ${changeDir}`,
    '',
    formatIssueInfo(issue),
    '',
    'Verify the review report is properly formatted and complete.',
  ].join('\n');

  return formatAgentPrompt({
    role: 'Verify the review report is properly formatted and complete',
    projectContext: agentConfig?.context,
    task: taskContent,
    instruction,
  });
}

export interface ExploreIssueInfo {
  title: string;
  body?: string;
  number?: number;
}

export function buildConflictResolutionPrompt(
  issue: Issue,
  changeDir: string,
  conflictFiles: string[],
  agentConfig?: AgentConfig,
): string {
  const instruction = loadFile(CONFLICT_RESOLUTION_PROMPT_PATH);

  const conflictFileList = conflictFiles
    .map((f) => `- ${f}`)
    .join('\n');

  const taskContent = [
    `Change Directory: ${changeDir}`,
    '',
    formatIssueInfo(issue),
    '',
    `Conflict Files:`,
    conflictFileList,
  ].join('\n');

  return formatAgentPrompt({
    role: 'Resolve merge conflicts',
    projectContext: agentConfig?.context,
    task: taskContent,
    contract: 'Apply ONLY the conflict resolution. Do NOT modify unrelated files.',
    instruction,
  });
}

export function buildAutoFixPrompt(
  issue: Issue,
  changeDir: string,
  reportContent: string,
  reportFileName: string,
  agentConfig?: AgentConfig,
): string {
  const instruction = loadFile(AUTO_FIX_PROMPT_PATH);

  const taskContent = [
    `Change Directory: ${changeDir}`,
    '',
    formatIssueInfo(issue),
    '',
    `The self-check report (${reportFileName}) produced a FAIL verdict.`,
    `Read the report below and apply ALL fix suggestions it describes.`,
    `Edit the relevant files in ${changeDir} to resolve every issue identified.`,
    '',
    `Report (${reportFileName}):`,
    reportContent,
  ].join('\n');

  return formatAgentPrompt({
    role: 'Apply auto-fixes from self-check report',
    projectContext: agentConfig?.context,
    task: taskContent,
    contract: 'Apply ONLY the fixes described in the report. Do NOT modify review.md.',
    instruction,
  });
}

export function buildExplorePrompt(
  issueInfo: ExploreIssueInfo,
  changeDir: string,
  existingProposal?: string | null,
  agentConfig?: AgentConfig,
): string {
  const instruction = loadFile(EXPLORE_PROMPT_PATH);

  const issueTitle = issueInfo.number
    ? `Issue #${issueInfo.number}: ${issueInfo.title}`
    : `Issue: ${issueInfo.title}`;

  const taskParts: string[] = [issueTitle];

  if (issueInfo.body) {
    taskParts.push('', issueInfo.body);
  }

  taskParts.push('', `Change Directory: ${changeDir}`);

  if (existingProposal) {
    taskParts.push(
      '',
      'The following proposal already exists. Update it based on your exploration:',
      '',
      existingProposal,
    );
  }

  const contextFiles: Array<{ path: string; desc: string }> = [];
  const designPath = path.join(changeDir, 'design.md');
  const specsDir = path.join(changeDir, 'specs');

  if (fs.existsSync(designPath)) {
    contextFiles.push({ path: designPath, desc: 'Existing design document' });
  }
  if (fs.existsSync(specsDir) && fs.statSync(specsDir).isDirectory()) {
    const specFiles = fs.readdirSync(specsDir, { recursive: true, encoding: 'utf-8' })
      .filter((entry): entry is string => typeof entry === 'string' && entry.endsWith('.md'));
    for (const specFile of specFiles) {
      contextFiles.push({
        path: path.join(specsDir, specFile),
        desc: `Existing spec: ${specFile}`,
      });
    }
  }

  return formatAgentPrompt({
    role: 'Explore the issue and codebase to understand the problem',
    projectContext: agentConfig?.context,
    contextFiles: contextFiles.length > 0 ? contextFiles : undefined,
    task: taskParts.join('\n'),
    instruction,
  });
}

export function buildReVerifyPrompt(
  issue: Issue,
  changeDir: string,
  reviewContent: string,
  agentConfig?: AgentConfig,
): string {
  const instruction = loadFile(RE_VERIFY_PROMPT_PATH);

  const taskContent = [
    `Change Directory: ${changeDir}`,
    '',
    formatIssueInfo(issue),
    '',
    'Perform a full re-review of all code changes after auto-fix.',
    'Focus on verifying that auto-fixes correctly resolved the original issues without introducing new problems.',
    '',
    'Review Report:',
    reviewContent,
  ].join('\n');

  return formatAgentPrompt({
    role: 'Re-verify code changes after auto-fix',
    projectContext: agentConfig?.context,
    task: taskContent,
    instruction,
  });
}
