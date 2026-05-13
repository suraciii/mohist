import * as fs from 'fs';
import * as path from 'path';
import type { Issue } from '../types';

export interface ContextFileRef {
  path: string;
  desc: string;
}

export function formatIssueInfo(issue: Issue): string {
  let info = `Issue #${issue.number}: ${issue.title}`;
  if (issue.body) {
    info += `\n\n${issue.body}`;
  }
  return info;
}

export function listOpenSpecContextFiles(changeDir: string | null | undefined, options: {
  includeReports?: boolean;
  includeSessionMemories?: boolean;
} = {}): ContextFileRef[] {
  if (!changeDir || !fs.existsSync(changeDir)) return [];

  const files: ContextFileRef[] = [];
  const addFile = (filePath: string, desc: string) => {
    if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
      files.push({ path: filePath, desc });
    }
  };

  addFile(path.join(changeDir, 'proposal.md'), 'Proposal - understand WHY this change is needed');
  addFile(path.join(changeDir, 'design.md'), 'Design - understand HOW this change should be implemented');

  const specsDir = path.join(changeDir, 'specs');
  if (fs.existsSync(specsDir) && fs.statSync(specsDir).isDirectory()) {
    const entries = fs.readdirSync(specsDir, { recursive: true, encoding: 'utf-8' });
    for (const entry of entries.sort()) {
      if (typeof entry === 'string' && entry.endsWith('.md')) {
        addFile(path.join(specsDir, entry), `Spec: ${entry} - requirements and acceptance criteria`);
      }
    }
  }

  addFile(path.join(changeDir, 'tasks.json'), 'Tasks - implementation plan, task status, and dependency graph');

  if (options.includeReports) {
    addFile(path.join(changeDir, 'self-review.md'), 'Self review - planning quality report and known plan findings');
    addFile(path.join(changeDir, 'review.md'), 'Review report - latest code review result and findings');
  }

  if (options.includeSessionMemories) {
    const memoriesDir = path.join(changeDir, 'session-memories');
    if (fs.existsSync(memoriesDir) && fs.statSync(memoriesDir).isDirectory()) {
      const entries = fs.readdirSync(memoriesDir).filter(entry => entry.endsWith('.json')).sort();
      for (const entry of entries) {
        addFile(path.join(memoriesDir, entry), `Previous task learning from ${path.basename(entry, '.json')}`);
      }
    }
  }

  return files;
}

