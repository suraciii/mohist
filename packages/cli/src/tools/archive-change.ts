import * as fs from 'fs';
import * as path from 'path';
import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';
import { detectOpenSpecChange } from '../openspec/detector';
import type { Issue } from '../types';

export interface ArchiveChangeContext {
  issue: Issue;
  worktreePath: string;
}

interface ExecutionReport {
  archivedAt: string;
  changeName: string;
  originalPath: string;
  archivePath: string;
  tasksCompleted: number;
  tasksFailed: number;
  sessionMemoriesCount: number;
  artifacts: string[];
}

function readTaskStatus(changePath: string): { completed: number; failed: number } {
  const taskStatusPath = path.join(changePath, 'task-status.json');
  if (!fs.existsSync(taskStatusPath)) {
    return { completed: 0, failed: 0 };
  }
  try {
    const content = fs.readFileSync(taskStatusPath, 'utf-8');
    const data = JSON.parse(content);
    const tasks = data.tasks || [];
    return {
      completed: tasks.filter((t: { status: string }) => t.status === 'completed').length,
      failed: tasks.filter((t: { status: string }) => t.status === 'failed').length,
    };
  } catch {
    return { completed: 0, failed: 0 };
  }
}

function countSessionMemories(changePath: string): number {
  const memoriesPath = path.join(changePath, 'session-memories');
  if (!fs.existsSync(memoriesPath)) {
    return 0;
  }
  try {
    const files = fs.readdirSync(memoriesPath);
    return files.filter(f => f.endsWith('.json')).length;
  } catch {
    return 0;
  }
}

function listArtifacts(changePath: string): string[] {
  const artifacts: string[] = [];
  try {
    const entries = fs.readdirSync(changePath, { withFileTypes: true });
    for (const entry of entries) {
      if (entry.isDirectory()) {
        artifacts.push(`${entry.name}/`);
      } else {
        artifacts.push(entry.name);
      }
    }
  } catch {
    // ignore
  }
  return artifacts;
}

function generateReport(changePath: string, changeName: string, archivePath: string): ExecutionReport {
  const taskStatus = readTaskStatus(changePath);
  const sessionMemoriesCount = countSessionMemories(changePath);
  const artifacts = listArtifacts(changePath);

  return {
    archivedAt: new Date().toISOString(),
    changeName,
    originalPath: changePath,
    archivePath,
    tasksCompleted: taskStatus.completed,
    tasksFailed: taskStatus.failed,
    sessionMemoriesCount,
    artifacts,
  };
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createArchiveChangeTool(context: ArchiveChangeContext): ToolInstance<any> {
  return Tool.define('archive_change', {
    description:
      'Archive the OpenSpec Change for this issue to `.mohist-specs/archive/`. ' +
      'This moves the Change directory to the archive location with a timestamp. ' +
      'Use this after the check stage approval is granted and the issue should be marked as done.',
    parameters: z.object({
      confirm: z.boolean().default(false).describe('Confirmation to archive (must be true)'),
    }),
    execute: async (params) => {
      if (!params.confirm) {
        return 'Error: archive_change requires confirm: true. This action cannot be undone.';
      }

      const change = detectOpenSpecChange(context.worktreePath, context.issue);
      if (!change) {
        return 'Error: No OpenSpec Change found for this issue. Nothing to archive.';
      }

      const changeName = path.basename(change.changePath);
      const archiveDir = path.join(context.worktreePath, '.mohist-specs', 'archive');

      if (!fs.existsSync(archiveDir)) {
        fs.mkdirSync(archiveDir, { recursive: true });
      }

      const now = new Date();
      const datePrefix = now.toISOString().split('T')[0];
      const archiveName = `${datePrefix}-${changeName}`;
      const archivePath = path.join(archiveDir, archiveName);

      if (fs.existsSync(archivePath)) {
        let version = 2;
        while (fs.existsSync(path.join(archiveDir, `${datePrefix}-${changeName}-v${version}`))) {
          version++;
        }
        const versionedName = `${datePrefix}-${changeName}-v${version}`;
        const versionedPath = path.join(archiveDir, versionedName);
        
        const report = generateReport(change.changePath, `${changeName}-v${version}`, versionedPath);
        
        fs.renameSync(change.changePath, versionedPath);
        
        const reportPath = path.join(versionedPath, 'execution-report.json');
        fs.writeFileSync(reportPath, JSON.stringify(report, null, 2), 'utf-8');

        return `Change "${changeName}" archived to ${versionedPath}\n` +
          `Execution report saved to ${reportPath}`;
      }

      const report = generateReport(change.changePath, changeName, archivePath);

      fs.renameSync(change.changePath, archivePath);

      const reportPath = path.join(archivePath, 'execution-report.json');
      fs.writeFileSync(reportPath, JSON.stringify(report, null, 2), 'utf-8');

      return `Change "${changeName}" archived to ${archivePath}\n` +
        `Execution report saved to ${reportPath}`;
    },
  });
}