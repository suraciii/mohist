import * as fs from 'fs';
import * as path from 'path';
import type { Issue } from '../types';

export type ArtifactType = 'proposal' | 'specs' | 'design' | 'tasks';

const ARTIFACTS_DIR = path.join(__dirname, 'prompts', 'artifacts');
const REVIEW_PROMPT_PATH = path.join(__dirname, 'prompts', 'review.md');
const EXPLORE_PROMPT_PATH = path.join(__dirname, 'prompts', 'explore.md');

const instructionCache = new Map<string, string>();

function loadInstruction(filePath: string): string {
  const cached = instructionCache.get(filePath);
  if (cached) return cached;

  if (!fs.existsSync(filePath)) {
    throw new Error(`Instruction file not found: ${filePath}`);
  }

  const content = fs.readFileSync(filePath, 'utf-8');
  instructionCache.set(filePath, content);
  return content;
}

function formatIssueInfo(issue: Issue): string {
  let info = `## Issue #${issue.number}: ${issue.title}\n`;
  if (issue.body) {
    info += `\n${issue.body}\n`;
  }
  return info;
}

export function buildArtifactPrompt(
  artifactType: ArtifactType,
  issue: Issue,
  changeDir: string
): string {
  const instructionFile = path.join(ARTIFACTS_DIR, `${artifactType}.md`);
  const instruction = loadInstruction(instructionFile);

  const parts: string[] = [
    formatIssueInfo(issue),
    '',
    `## Change Directory\n\n${changeDir}`,
    '',
    `## Goal\n\nGenerate the **${artifactType}** artifact.`,
    '',
    '## Instructions\n',
    instruction,
    '',
    'Tip: You can use read_file to view previously generated artifacts in the change directory.',
  ];

  return parts.join('\n');
}

export function buildSelfReviewPrompt(
  issue: Issue,
  changeDir: string
): string {
  const instructionFile = path.join(ARTIFACTS_DIR, 'self-review.md');
  const instruction = loadInstruction(instructionFile);

  const parts: string[] = [
    formatIssueInfo(issue),
    '',
    `## Change Directory\n\n${changeDir}`,
    '',
    '## Goal\n\nSelf-review all generated artifacts.',
    '',
    '## Instructions\n',
    instruction,
  ];

  return parts.join('\n');
}

export function buildReviewerPrompt(
  issue: Issue,
  changeDir: string
): string {
  const instruction = loadInstruction(REVIEW_PROMPT_PATH);

  const parts: string[] = [
    formatIssueInfo(issue),
    '',
    `## Change Directory\n\n${changeDir}`,
    '',
    '## Goal\n\nReview the implementation for quality.',
    '',
    '## Instructions\n',
    instruction,
  ];

  return parts.join('\n');
}

export interface ExploreIssueInfo {
  title: string;
  body?: string;
  number?: number;
}

export function buildExplorePrompt(
  issueInfo: ExploreIssueInfo,
  changeDir: string,
  existingProposal?: string | null,
): string {
  const instruction = loadInstruction(EXPLORE_PROMPT_PATH);

  const parts: string[] = [];

  if (issueInfo.number) {
    parts.push(`## Issue #${issueInfo.number}: ${issueInfo.title}`);
  } else {
    parts.push(`## Issue: ${issueInfo.title}`);
  }

  if (issueInfo.body) {
    parts.push('', issueInfo.body);
  }

  parts.push('', `## Change Directory\n\n${changeDir}`);

  if (existingProposal) {
    parts.push(
      '',
      '## Existing Proposal\n\nThe following proposal already exists. Update it based on your exploration:\n',
      existingProposal,
    );
  }

  parts.push('', '## Instructions\n', instruction);

  return parts.join('\n');
}
