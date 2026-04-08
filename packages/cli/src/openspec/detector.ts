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

export function detectOpenSpecChange(worktreePath: string, issue: Issue): OpenSpecChange | null {
  const changeDir = path.join(worktreePath, '.mohist-specs', 'changes');
  
  if (!fs.existsSync(changeDir)) {
    return null;
  }
  
  const entries = fs.readdirSync(changeDir, { withFileTypes: true });
  const changeDirs = entries
    .filter(e => e.isDirectory())
    .map(e => e.name);
  
  const issuePrefix = `${issue.number}-`;
  const matchingChange = changeDirs.find(dir => dir.startsWith(issuePrefix));
  
  if (!matchingChange) {
    return null;
  }
  
  const changePath = path.join(changeDir, matchingChange);
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
