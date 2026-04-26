import * as fs from 'fs';
import * as path from 'path';
import type { Issue } from '../types';

export type ArtifactType = 'proposal' | 'specs' | 'design' | 'tasks';

const ARTIFACTS_DIR = path.join(__dirname, 'prompts', 'artifacts');
const TEMPLATES_DIR = path.join(ARTIFACTS_DIR, 'templates');
const REVIEW_PROMPT_PATH = path.join(__dirname, 'prompts', 'review.md');
const REVIEW_SELF_CHECK_PATH = path.join(ARTIFACTS_DIR, 'review-self-check.md');
const EXPLORE_PROMPT_PATH = path.join(__dirname, 'prompts', 'explore.md');
const CONFLICT_RESOLUTION_PROMPT_PATH = path.join(__dirname, 'prompts', 'conflict-resolution.md');

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
  const tasksPath = path.join(changeDir, 'tasks.json');

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

  if (fs.existsSync(tasksPath)) {
    const content = fs.readFileSync(tasksPath, 'utf-8');
    parts.push(`## Tasks & Acceptance Criteria\n\n${content}`);
  }

  return parts.join('\n\n');
}

function formatIssueInfo(issue: Issue): string {
  let info = `## Issue #${issue.number}: ${issue.title}\n`;
  if (issue.body) {
    info += `\n${issue.body}\n`;
  }
  return info;
}

function buildDependencies(artifactType: ArtifactType, changeDir: string): string {
  const myIndex = DEPENDENCY_ORDER.indexOf(artifactType);
  const lines: string[] = ['Read these files for context:'];

  for (let i = 0; i < myIndex; i++) {
    const depType = DEPENDENCY_ORDER[i];
    const depFile = ARTIFACT_OUTPUT_FILES[depType];
    const depPath = path.join(changeDir, depFile);
    if (fs.existsSync(depPath)) {
      lines.push(`- ${depPath}`);
    }
  }

  if (lines.length === 1) {
    return 'No previous artifacts to reference.';
  }

  return lines.join('\n');
}

export function buildArtifactPrompt(
  artifactType: ArtifactType,
  issue: Issue,
  changeDir: string
): string {
  const instructionFile = path.join(ARTIFACTS_DIR, `${artifactType}.md`);
  const instruction = loadFile(instructionFile);

  const templateFile = path.join(TEMPLATES_DIR, `${artifactType}.tpl.md`);
  const template = loadFile(templateFile);

  const outputFile = ARTIFACT_OUTPUT_FILES[artifactType];
  const outputPath = path.join(changeDir, outputFile);
  const dependencies = buildDependencies(artifactType, changeDir);

  const parts: string[] = [
    formatIssueInfo(issue),
    '',
    `<task>`,
    `Create the ${artifactType} artifact for this change.`,
    ARTIFACT_DESCRIPTIONS[artifactType],
    `</task>`,
    '',
    `<dependencies>`,
    dependencies,
    `</dependencies>`,
    '',
    `<output>`,
    `Write to: ${outputPath}`,
    `</output>`,
    '',
    `<template>`,
    template,
    `</template>`,
    '',
    `<instruction>`,
    instruction,
    `</instruction>`,
  ];

  return parts.join('\n');
}

export function buildSelfReviewPrompt(
  issue: Issue,
  changeDir: string
): string {
  const instructionFile = path.join(ARTIFACTS_DIR, 'self-review.md');
  const instruction = loadFile(instructionFile);

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
  const instruction = loadFile(REVIEW_PROMPT_PATH);
  const specContext = loadSpecContext(changeDir);

  const parts: string[] = [
    formatIssueInfo(issue),
    '',
    `## Change Directory\n\n${changeDir}`,
  ];

  if (specContext) {
    parts.push('', specContext);
  }

  parts.push(
    '',
    '## Goal\n\nReview the implementation for quality.',
    '',
    '## Instructions\n',
    instruction,
  );

  return parts.join('\n');
}

export function buildReviewSelfCheckPrompt(
  issue: Issue,
  changeDir: string
): string {
  const instruction = loadFile(REVIEW_SELF_CHECK_PATH);

  const parts: string[] = [
    formatIssueInfo(issue),
    '',
    `## Change Directory\n\n${changeDir}`,
    '',
    '## Goal\n\nVerify the review report is properly formatted and complete.',
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

export function buildConflictResolutionPrompt(
  issue: Issue,
  changeDir: string,
  conflictFiles: string[],
): string {
  const instruction = loadFile(CONFLICT_RESOLUTION_PROMPT_PATH);

  const conflictFileList = conflictFiles
    .map((f) => `- ${f}`)
    .join('\n');

  const parts: string[] = [
    formatIssueInfo(issue),
    '',
    `## Change Directory\n\n${changeDir}`,
    '',
    '## Conflict Files\n',
    conflictFileList,
    '',
    '## Instructions\n',
    instruction,
  ];

  return parts.join('\n');
}

export function buildExplorePrompt(
  issueInfo: ExploreIssueInfo,
  changeDir: string,
  existingProposal?: string | null,
): string {
  const instruction = loadFile(EXPLORE_PROMPT_PATH);

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
