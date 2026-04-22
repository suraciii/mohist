import * as fs from 'fs';
import * as path from 'path';
import type { Issue } from '../types';

export type ArtifactType = 'proposal' | 'specs' | 'design' | 'tasks';

const ARTIFACTS_DIR = path.join(__dirname, 'prompts', 'artifacts');
const TEMPLATES_DIR = path.join(ARTIFACTS_DIR, 'templates');
const REVIEW_PROMPT_PATH = path.join(__dirname, 'prompts', 'review.md');
const EXPLORE_PROMPT_PATH = path.join(__dirname, 'prompts', 'explore.md');

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
