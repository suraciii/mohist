import * as fs from 'fs';
import * as path from 'path';
import type { Issue } from '../types';

export interface OpenSpecChange {
  changePath: string;
  prdPath: string;
  taskStatusPath: string;
  sessionMemoriesPath: string;
  proposalPath: string;
  designPath: string;
  specsPath: string;
}

function extractVersion(dirName: string): number {
  const match = dirName.match(/-v(\d+)$/);
  return match ? parseInt(match[1], 10) : 1;
}

function getSlug(dirName: string, issuePrefix: string): string | null {
  if (!dirName.startsWith(issuePrefix)) return null;
  const suffix = dirName.slice(issuePrefix.length);
  if (!suffix) return null;
  const versionMatch = suffix.match(/^(.+)-v(\d+)$/);
  return versionMatch ? versionMatch[1] : suffix;
}

function findMatchingChanges(changeDirs: string[], issuePrefix: string): string[] {
  const groups = new Map<string, string[]>();

  for (const dir of changeDirs) {
    const slug = getSlug(dir, issuePrefix);
    if (slug === null) continue;
    if (!groups.has(slug)) groups.set(slug, []);
    groups.get(slug)!.push(dir);
  }

  return Array.from(groups.values()).map(dirs => {
    return dirs.sort((a, b) => extractVersion(b) - extractVersion(a))[0];
  });
}

export function findChangeDir(cwd: string, issueNumber: number): string | null {
  const changesDir = path.join(cwd, '.mohist-specs', 'changes');

  if (!fs.existsSync(changesDir)) {
    return null;
  }

  const entries = fs.readdirSync(changesDir, { withFileTypes: true });
  const changeDirs = entries
    .filter(e => e.isDirectory())
    .map(e => e.name);

  const issuePrefix = `${issueNumber}-`;
  const matchingChanges = findMatchingChanges(changeDirs, issuePrefix);

  if (matchingChanges.length === 0) {
    return null;
  }

  const bestMatch = matchingChanges.length === 1
    ? matchingChanges[0]
    : matchingChanges.sort((a, b) => extractVersion(b) - extractVersion(a))[0];

  return path.join(changesDir, bestMatch);
}

export function detectOpenSpecChange(worktreePath: string, issue: Issue): OpenSpecChange | null {
  const changePath = findChangeDir(worktreePath, issue.number);

  if (!changePath) {
    return null;
  }

  const prdPath = path.join(changePath, 'prd.json');

  if (!fs.existsSync(prdPath)) {
    return null;
  }

  return {
    changePath,
    prdPath,
    taskStatusPath: path.join(changePath, 'task-status.json'),
    sessionMemoriesPath: path.join(changePath, 'session-memories'),
    proposalPath: path.join(changePath, 'proposal.md'),
    designPath: path.join(changePath, 'design.md'),
    specsPath: path.join(changePath, 'specs'),
  };
}
