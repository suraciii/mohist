import { Command } from 'commander';
import chalk from 'chalk';
import { execSync, spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { ApiResponse, Issue, Priority, VALID_PRIORITIES } from '../../types';
import { slugify } from '../../utils/slugify';
import { apiClient } from '../api-client';
import { requireServer } from '../server-check';
import { classifyMergeDelivery, type MergeDeliveryStatus } from '../../workflow/issue-lifecycle';

function latestCurrentTruthChecks(checkResults: any[]): any[] {
  const latestByName = new Map<string, any>();
  for (const check of checkResults) {
    latestByName.set(check.name, check);
  }

  const rendered = new Set<string>();
  return checkResults.filter((check) => {
    if (latestByName.get(check.name) !== check) return false;
    if (rendered.has(check.name)) return false;
    rendered.add(check.name);
    return true;
  });
}

function formatStage(stage: string): string {
  const colors: Record<string, typeof chalk.green> = {
    draft: chalk.gray,
    plan: chalk.blue,
    build: chalk.cyan,
    check: chalk.yellow,
    done: chalk.green
  };
  
  const color = colors[stage] || chalk.white;
  return color(stage);
}

function formatStatus(status: string): string {
  const colors: Record<string, typeof chalk.green> = {
    active: chalk.green,
    paused: chalk.yellow,
    blocked: chalk.red
  };
  
  const color = colors[status] || chalk.white;
  return color(status);
}

function formatPriority(priority: string): string {
  const colors: Record<string, typeof chalk.green> = {
    p0: chalk.red.bold,
    p1: chalk.red,
    p2: chalk.yellow,
    p3: chalk.green,
    p4: chalk.gray,
  };
  const color = colors[priority] || chalk.white;
  return color(priority);
}

function formatArchivedAt(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleDateString();
  } catch {
    return iso;
  }
}

function formatLabels(labels: string[]): string {
  if (!labels || labels.length === 0) return '';
  return labels.map(l => chalk.blue(`[${l}]`)).join(' ');
}

function parseLabelFlags(flags: string[] | undefined): { add: string[]; remove: string[] } {
  const add: string[] = [];
  const remove: string[] = [];
  
  if (flags) {
    for (const flag of flags) {
      if (flag.startsWith('+')) {
        add.push(flag.slice(1));
      } else if (flag.startsWith('-')) {
        remove.push(flag.slice(1));
      } else {
        add.push(flag);
      }
    }
  }
  
  return { add, remove };
}

export function setupIssueCommands(program: Command): void {
  const issue = program.command('issue').description('Manage issues');

  issue.hook('preAction', async () => {
    await requireServer();
  });

  issue
    .command('create <title>')
    .description('Create a new issue')
    .option('-b, --body <body>', 'Issue body/description')
    .option('-l, --label <label>', 'Add label (can be repeated)', (val, prev: string[]) => [...prev, val], [] as string[])
    .option('-p, --priority <level>', 'Set priority (p0-p4)')
    .action(async (title, options) => {
      try {
        if (options.priority && !VALID_PRIORITIES.includes(options.priority as Priority)) {
          console.error(chalk.red(`Invalid priority: ${options.priority}. Must be one of: ${VALID_PRIORITIES.join(', ')}`));
          return;
        }
        const response = await apiClient<ApiResponse<Issue>>(
          'POST',
          '/issues',
          { title, body: options.body, labels: options.label, priority: options.priority }
        );
        
        if (response.success && response.data) {
          const issue = response.data;
          console.log(chalk.green(`✓ Created issue #${issue.number}: ${issue.title}`));
          console.log(chalk.gray(`  Priority: ${formatPriority(issue.priority)}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to create issue: ${error}`));
      }
    });

  issue
    .command('list')
    .description('List issues')
    .option('-s, --status <stage>', 'Filter by stage')
    .option('-l, --label <label>', 'Filter by label')
    .option('-p, --priority <level>', 'Filter by priority')
    .option('--archived', 'Show only archived issues')
    .option('--all', 'Show all issues including archived')
    .action(async (options) => {
      try {
        let path = '/issues';
        const params: string[] = [];
        if (options.status) {
          params.push(`stage=${options.status}`);
        }
        if (options.label) {
          params.push(`label=${options.label}`);
        }
        if (options.priority) {
          params.push(`priority=${options.priority}`);
        }
        if (options.archived) {
          params.push('archived=true');
        } else if (options.all) {
          params.push('all=true');
        }
        if (params.length > 0) {
          path += `?${params.join('&')}`;
        }
        
        const response = await apiClient<ApiResponse<Issue[]>>('GET', path);
        
        if (response.success && response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow(options.archived ? 'No archived issues' : 'No issues found'));
            return;
          }
          
          const header = options.archived ? 'Archived Issues:' : 'Issues:';
          console.log(chalk.bold(`\n${header}\n`));

          if (options.archived || options.all) {
            console.log('  ID                 Priority  Stage                     Status    Labels              Title                                       Archived');
            console.log('  ' + '─'.repeat(130));
          } else {
            console.log('  ID                 Priority  Stage                     Status    Labels              Title');
            console.log('  ' + '─'.repeat(105));
          }
          
          response.data.forEach((issue: any) => {
            const id = chalk.cyan(`${issue.projectName || 'unknown'}#${issue.number}`.padEnd(18));
            const priority = formatPriority(issue.priority || 'p2').padEnd(9);
            const stage = formatStage(issue.stage).padEnd(25);
            const status = formatStatus(issue.status).padEnd(9);
            const labels = formatLabels(issue.labels).padEnd(20);
            const titlePart = issue.title.substring(0, 40);

            const mergeStatus = classifyMergeDelivery(issue);
            const mergeWarning = mergeStatus === 'done-not-merged' ? chalk.red.bold(' [UNMERGED]') : '';

            if (options.archived || options.all) {
              const archivedCol = issue.archivedAt
                ? chalk.gray(formatArchivedAt(issue.archivedAt)).padEnd(24)
                : '';
              const suffix = issue.archivedAt && !options.archived ? chalk.gray(' (archived)') : '';
              console.log(`  ${id} ${priority} ${stage} ${status} ${labels} ${titlePart.padEnd(42)}${archivedCol}${suffix}${mergeWarning}`);
            } else {
              console.log(`  ${id} ${priority} ${stage} ${status} ${labels} ${titlePart}${mergeWarning}`);
            }
          });
          console.log();
        }
      } catch (error) {
        console.error(chalk.red(`Failed to list issues: ${error}`));
      }
    });

  issue
    .command('show <number>')
    .description('Show issue details')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<any>>(
          'GET',
          `/issues/${number}`
        );

        if (response.success && response.data) {
          const issue = response.data;
          const displayId = `${issue.projectName || 'unknown'}#${issue.number}`;
          console.log(chalk.bold(`\nIssue ${displayId}: ${issue.title}\n`));
          console.log(`  Priority: ${formatPriority(issue.priority || 'p2')}`);
          console.log(`  Stage: ${formatStage(issue.stage)}`);
          console.log(`  Status: ${formatStatus(issue.status)}`);

          const mergeStatus = classifyMergeDelivery(issue);
          const mergeStatusColors: Record<MergeDeliveryStatus, typeof chalk.green> = {
            merged: chalk.green,
            queued: chalk.blue,
            rebasing: chalk.blue,
            merging: chalk.blue,
            resolving: chalk.yellow,
            conflict: chalk.red,
            'build-failed': chalk.red,
            blocked: chalk.red,
            'not-ready': chalk.gray,
            'not-merged': chalk.yellow,
            unknown: chalk.gray,
            'done-not-merged': chalk.red,
            integrating: chalk.cyan,
          };
          const mergeColor = mergeStatusColors[mergeStatus] || chalk.white;
          const mergeStatusLabels: Record<MergeDeliveryStatus, string> = {
            merged: 'Merged',
            queued: 'Queued for merge',
            rebasing: 'Rebasing',
            merging: 'Merging',
            resolving: 'Resolving conflicts',
            conflict: 'Merge conflict',
            'build-failed': 'Build failed after merge',
            blocked: 'Blocked',
            'not-ready': 'Not ready for merge',
            'not-merged': 'Not merged',
            unknown: 'Unknown',
            'done-not-merged': 'DONE BUT NOT MERGED',
            integrating: 'Integrating',
          };
          console.log(`  Merge: ${mergeColor(mergeStatusLabels[mergeStatus])}`);

          if (issue.baseBranch) {
            const sourceBranch = `mo/issue-${issue.number}`;
            const targetBranch = issue.baseBranch;
            console.log(`  Branch: ${chalk.cyan(sourceBranch)} → ${chalk.cyan(targetBranch)}`);
          }

          if (mergeStatus === 'done-not-merged') {
            console.log(chalk.red.bold('  WARNING: This issue is marked done/completed but has not been merged!'));
          }

          if (issue.archivedAt) {
            console.log(`  Archived: ${chalk.gray(new Date(issue.archivedAt).toLocaleString())}`);
          }

          const executionsResponse = await apiClient<ApiResponse<any[]>>(
            'GET',
            `/issues/${number}/executions`
          );

          if (executionsResponse.success && executionsResponse.data && executionsResponse.data.length > 0) {
            console.log(chalk.gray('\n  Stage Checks:'));

            const latestByStage = new Map<string, any>();
            for (const execution of executionsResponse.data) {
              latestByStage.set(execution.stage, execution);
            }

            for (const execution of latestByStage.values()) {
              if (execution.checkResults && execution.checkResults.length > 0) {
                console.log(chalk.gray(`    [${execution.stage}]`));
                for (const check of latestCurrentTruthChecks(execution.checkResults)) {
                  const isHealthGate = check.name.startsWith('health:') || (check.output && (check.output as any).kind === 'health-gate');
                  const checkIcon = check.status === 'pass' ? chalk.green('✓') : check.status === 'fail' ? chalk.red('✗') : check.status === 'error' ? chalk.red('✗') : chalk.gray('○');
                  const displayName = isHealthGate ? `health:${execution.stage}` : check.name;
                  console.log(`      ${checkIcon} ${displayName}`);
                  if (check.status === 'fail' || check.status === 'error') {
                    if (check.message) {
                      console.log(chalk.red(`        Error: ${check.message}`));
                    }
                    if (isHealthGate && check.output) {
                      const output = check.output as any;
                      if (output.command) console.log(chalk.gray(`        Command: ${output.command}`));
                      if (output.duration) console.log(chalk.gray(`        Duration: ${output.duration}ms`));
                      if (output.summary) console.log(chalk.red(`        Summary: ${output.summary}`));
                      if (output.logExcerpt) {
                        const excerptLines = output.logExcerpt.split('\n').slice(0, 5);
                        if (excerptLines.length > 0) {
                          console.log(chalk.gray(`        Log excerpt:`));
                          for (const line of excerptLines) {
                            console.log(chalk.gray(`          ${line}`));
                          }
                        }
                      }
                    }
                  }
                  if (check.duration) {
                    console.log(chalk.gray(`        Duration: ${check.duration}ms`));
                  }
                }
              }
            }
          }

          if (issue.approvalState) {
            const statusColors: Record<string, typeof chalk.green> = {
              awaiting: chalk.yellow,
              approved: chalk.green,
              rejected: chalk.red,
              error: chalk.red,
            };
            const as = issue.approvalState;
            const color = statusColors[as.status] || chalk.white;
            console.log(`  Approval: ${color(as.status)} (stage: ${as.stage})`);
            if (as.status === 'error' && as.output?.error) {
              console.log(`  ${chalk.red(`Error: ${as.output.error}`)}`);
            } else if (as.output) {
              const notes = typeof as.output === 'string' ? as.output : JSON.stringify(as.output, null, 2);
              console.log(`  Self-review notes:`);
              console.log(`  ${chalk.gray(notes.split('\n').join('\n  '))}`);
            }
          }
          
          if (issue.labels && issue.labels.length > 0) {
            console.log(`  Labels: ${formatLabels(issue.labels)}`);
          }
          
          if (issue.body) {
            console.log(`\n  ${chalk.gray('Body:')}`);
            console.log(`  ${issue.body.split('\n').join('\n  ')}`);
          }
          
          if (issue.comments && issue.comments.length > 0) {
            console.log(`\n  ${chalk.gray('Comments:')} (${issue.comments.length})`);
            issue.comments.forEach((comment: any) => {
              const shortId = comment.id.substring(0, 8);
              console.log(`    [${chalk.cyan(shortId)}] ${chalk.gray(new Date(comment.createdAt).toLocaleString())}`);
              console.log(`    ${comment.body.split('\n').join('\n    ')}`);
              console.log();
            });
          }
          
          console.log();
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show issue: ${error}`));
      }
    });

  issue
    .command('update <number>')
    .description('Update an issue')
    .option('--title <title>', 'New title')
    .option('--body <body>', 'New body')
    .option('-l, --label <label>', 'Add (+label) or remove (-label) label', (val, prev: string[]) => [...prev, val], [] as string[])
    .option('-p, --priority <level>', 'Set priority (p0-p4)')
    .action(async (number, options) => {
      try {
        if (options.priority && !VALID_PRIORITIES.includes(options.priority as Priority)) {
          console.error(chalk.red(`Invalid priority: ${options.priority}. Must be one of: ${VALID_PRIORITIES.join(', ')}`));
          return;
        }
        const { add, remove } = parseLabelFlags(options.label);
        
        const response = await apiClient<ApiResponse<Issue>>(
          'PATCH',
          `/issues/${number}`,
          {
            title: options.title,
            body: options.body,
            priority: options.priority,
            addLabels: add.length > 0 ? add : undefined,
            removeLabels: remove.length > 0 ? remove : undefined,
          }
        );
        
        if (response.success && response.data) {
          console.log(chalk.green(`✓ Updated issue #${response.data.number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to update issue: ${error}`));
      }
    });

  issue
    .command('close <number>')
    .description('Close an issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/close`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Closed issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to close issue: ${error}`));
      }
    });

  issue
    .command('archive')
    .description('Archive an issue (or all completed issues)')
    .option('--all-completed', 'Archive all completed issues')
    .option('--no-cleanup', 'Skip worktree and openspec cleanup')
    .argument('[number]', 'Issue number to archive')
    .action(async (number, options) => {
      try {
        if (options.allCompleted) {
          if (options.noCleanup) {
            console.error(chalk.red('Error: --no-cleanup is not supported for batch archive. Use "mo issue archive <number> --no-cleanup" for single-issue archive without cleanup.'));
            return;
          }

          const response = await apiClient<ApiResponse>(
            'POST',
            '/issues/archive-completed'
          );

          if (response.success && response.data) {
            const { archived = 0, skipped = 0, skippedNumbers = [], message } = response.data as any;
            if (archived === 0 && skipped === 0) {
              console.log(chalk.yellow('No completed issues to archive'));
            } else {
              console.log(chalk.green(`✓ Archived ${archived} completed issue(s)`));
              if (skipped > 0) {
                console.log(chalk.yellow(`  Skipped ${skipped} false-done issue(s) (not merged): #${skippedNumbers.join(', #')}`));
              }
            }
            if (message) {
              console.log(chalk.gray(`  ${message}`));
            }
          } else {
            console.error(chalk.red(`Error: ${response.error}`));
          }
          return;
        }

        if (!number) {
          console.error(chalk.red('Error: provide an issue number or --all-completed'));
          return;
        }

        const body: { cleanup: boolean } = { cleanup: options.cleanup !== false };
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/archive`,
          body
        );

        if (response.success) {
          console.log(chalk.green(`✓ Archived issue #${number}`));
          if (response.data?.warning) {
            console.log(chalk.yellow(`  ${response.data.warning}`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to archive issue: ${error}`));
      }
    });

  issue
    .command('unarchive <number>')
    .description('Unarchive an issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/unarchive`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Unarchived issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to unarchive issue: ${error}`));
      }
    });

  issue
    .command('reopen <number>')
    .description('Reopen a blocked, closed, or paused issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/reopen`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Reopened issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to reopen issue: ${error}`));
      }
    });

  issue
    .command('comment <number> <text>')
    .description('Add a comment to an issue')
    .action(async (number, text) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/comments`,
          { body: text }
        );

        if (response.success) {
          console.log(chalk.green(`✓ Comment added to issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to add comment: ${error}`));
      }
    });

  issue
    .command('delete-comment <number> <comment-id>')
    .description('Delete a comment from an issue')
    .action(async (number, commentId) => {
      try {
        const response = await apiClient<ApiResponse>(
          'DELETE',
          `/issues/${number}/comments/${commentId}`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Deleted comment ${commentId} from issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to delete comment: ${error}`));
      }
    });

  issue
    .command('start <number>')
    .description('Start processing an issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/start`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Started processing issue #${number}`));
          if (response.data?.taskId) {
            console.log(chalk.gray(`  Task ID: ${response.data.taskId}`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to start issue: ${error}`));
      }
    });

  issue
    .command('approve <number>')
    .description('Approve an issue awaiting approval')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<any>>(
          'POST',
          `/issues/${number}/approve`
        );

        if (response.success) {
          const message = response.data?.message;
          if (message) {
            console.log(chalk.green(`✓ ${message}`));
          } else {
            console.log(chalk.green(`✓ Issue #${number} approved`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to approve issue: ${error}`));
      }
    });

  issue
    .command('reject <number>')
    .description('Reject an issue awaiting approval')
    .option('-m, --message <message>', 'Rejection reason')
    .action(async (number, options) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/reject`,
          { message: options.message }
        );
        
        if (response.success) {
          console.log(chalk.yellow(`✓ Issue #${number} rejected, pipeline will restart`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to reject issue: ${error}`));
      }
    });

  issue
    .command('resume <number>')
    .description('Resume a paused issue with optional skip to review')
    .option('--skip-to-review', 'Skip plan stage and go directly to review (for OpenSpec workflow)')
    .action(async (number, options) => {
      try {
        const endpoint = options.skipToReview 
          ? `/issues/${number}/skip-to-review` 
          : `/issues/${number}/reopen`;
        
        const response = await apiClient<ApiResponse>(
          'POST',
          endpoint
        );
        
        if (response.success) {
          if (options.skipToReview) {
            console.log(chalk.green(`✓ Issue #${number} resumed, skipped to review stage`));
          } else {
            console.log(chalk.green(`✓ Issue #${number} reopened`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to resume issue: ${error}`));
      }
    });

  issue
    .command('diff <number>')
    .description('Show diff between issue branch and main')
    .action(async (number) => {
      try {
        const detailResponse = await apiClient<ApiResponse<any>>(
          'GET',
          `/issues/${number}`
        );

        if (!detailResponse.success || !detailResponse.data) {
          console.error(chalk.red(`Error: ${detailResponse.error}`));
          return;
        }

        const issue = detailResponse.data;
        const projectPath = issue.projectPath;
        if (!projectPath) {
          console.error(chalk.red('Error: No project path found'));
          return;
        }

        const branchName = `mo/issue-${number}`;
        const baseBranch = issue.baseBranch || 'main';

        try {
          execSync(`git diff ${baseBranch}...${branchName}`, {
            cwd: projectPath,
            stdio: 'inherit'
          });
        } catch (error: any) {
          const message = error.message || String(error);
          if (message.includes('unknown revision') || message.includes('not found')) {
            console.error(chalk.red(`No worktree found for issue #${number}`));
          } else {
            console.error(chalk.red(`Failed to show diff: ${message}`));
          }
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show diff: ${error}`));
      }
    });

  const PIPELINE_EVENT_TYPES = 'build_started,build_completed,build_failed,task_started,task_completed,task_failed';

  function formatPipelineEventType(eventType: string): string {
    const labels: Record<string, string> = {
      build_started: 'BUILD START',
      build_completed: 'BUILD DONE',
      build_failed: 'BUILD FAIL',
      task_started: 'TASK START',
      task_completed: 'TASK DONE',
      task_failed: 'TASK FAIL',
    };
    return labels[eventType] || eventType;
  }

  function formatPipelineEventTimestamp(createdAt: string): string {
    try {
      const d = new Date(createdAt);
      return d.toLocaleString();
    } catch {
      return createdAt;
    }
  }

  function summarizePipelineEvent(event: any): string {
    const data = event.data || {};
    switch (event.eventType) {
      case 'build_started':
        return `Build stage started${data.changeId ? ` (change: ${data.changeId})` : ''}`;
      case 'build_completed':
        return `Build stage completed${data.completed != null && data.total != null ? ` (${data.completed}/${data.total} tasks)` : ''}`;
      case 'build_failed':
        return `Build stage failed${data.error ? `: ${data.error}` : ''}`;
      case 'task_started':
        return `Task started: ${data.taskId || data.title || 'unknown'}${data.title ? ` - ${data.title}` : ''}`;
      case 'task_completed':
        return `Task completed: ${data.taskId || 'unknown'}${data.title ? ` - ${data.title}` : ''}`;
      case 'task_failed':
        return `Task failed: ${data.taskId || 'unknown'}${data.error ? ` - ${data.error}` : ''}`;
      default:
        return JSON.stringify(data);
    }
  }

  issue
    .command('logs <number>')
    .description('Show agent logs for an issue')
    .option('-f, --follow', 'Follow log output in real time')
    .action(async (number, options) => {
      try {
        const detailResponse = await apiClient<ApiResponse<any>>(
          'GET',
          `/issues/${number}`
        );

        if (!detailResponse.success || !detailResponse.data) {
          console.error(chalk.red(`Error: ${detailResponse.error}`));
          return;
        }

        const issue = detailResponse.data;

        let hasPipelineEvents = false;
        try {
          const logsResponse = await apiClient<ApiResponse<any[]>>(
            'GET',
            `/issues/${number}/logs?eventType=${PIPELINE_EVENT_TYPES}`
          );

          if (logsResponse.success && logsResponse.data && logsResponse.data.length > 0) {
            hasPipelineEvents = true;
            console.log(chalk.bold('\nPipeline Events:\n'));
            console.log(`  ${'Timestamp'.padEnd(22)} ${'Event'.padEnd(14)} Summary`);
            console.log('  ' + '─'.repeat(80));
            for (const event of logsResponse.data) {
              const ts = formatPipelineEventTimestamp(event.createdAt).padEnd(22);
              const typeLabel = formatPipelineEventType(event.eventType);
              const typeColor: Record<string, typeof chalk.green> = {
                'BUILD START': chalk.blue,
                'BUILD DONE': chalk.green,
                'BUILD FAIL': chalk.red,
                'TASK START': chalk.cyan,
                'TASK DONE': chalk.green,
                'TASK FAIL': chalk.red,
              };
              const colored = (typeColor[typeLabel] || chalk.white)(typeLabel).padEnd(22);
              const summary = summarizePipelineEvent(event);
              console.log(`  ${ts} ${colored} ${summary}`);
            }
            console.log();
          }
        } catch {
          // pipeline events unavailable, continue to file logs
        }

        if (options.follow) {
          if (hasPipelineEvents) {
            console.log(chalk.yellow('Note: --follow shows file logs only. Pipeline events are above.\n'));
          }
        }

        const projectName = issue.projectName;
        if (!projectName || projectName === 'unknown') {
          if (!hasPipelineEvents) {
            console.error(chalk.red('Error: No project name found'));
          }
          return;
        }

        const slug = slugify(projectName);
        const home = process.env.HOME || '';
        const logDir = path.join(home, '.mohist', 'projects', slug, 'logs', `issue-${number}`);

        if (!fs.existsSync(logDir)) {
          if (!hasPipelineEvents) {
            console.error(chalk.red(`No logs found for issue #${number}`));
          }
          return;
        }

        const logFiles = fs.readdirSync(logDir)
          .filter(f => f.endsWith('.log'))
          .sort();

        if (logFiles.length === 0) {
          if (!hasPipelineEvents) {
            console.error(chalk.red(`No logs found for issue #${number}`));
          }
          return;
        }

        if (options.follow) {
          const logFile = logFiles[logFiles.length - 1];
          const logPath = path.join(logDir, logFile);

          const tail = spawn('tail', ['-f', '-n', '50', logPath], {
            stdio: 'inherit'
          });

          tail.on('error', (err) => {
            console.error(chalk.red(`Failed to tail logs: ${err.message}`));
          });

          process.on('SIGINT', () => {
            tail.kill();
            process.exit(0);
          });
        } else {
          console.log(chalk.bold('Agent Session Logs:\n'));
          for (const logFile of logFiles) {
            const logPath = path.join(logDir, logFile);
            const content = fs.readFileSync(logPath, 'utf-8');
            const lines = content.split('\n');
            const lastLines = lines.slice(-50);
            console.log(chalk.bold(`--- ${logFile} ---`));
            console.log(lastLines.join('\n'));
            console.log();
          }
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show logs: ${error}`));
      }
    });
}

export function setupLabelCommands(program: Command): void {
  const label = program.command('label').description('Manage labels');

  label.hook('preAction', async () => {
    await requireServer();
  });

  label
    .command('list')
    .description('List all labels used in the current project')
    .action(async () => {
      try {
        const response = await apiClient<ApiResponse<string[]>>(
          'GET',
          '/labels'
        );
        
        if (response.success && response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow('No labels found'));
            return;
          }
          
          console.log(chalk.bold('\nLabels:\n'));
          response.data.forEach((labelName) => {
            console.log(`  ${chalk.blue(`[${labelName}]`)}`);
          });
          console.log();
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to list labels: ${error}`));
      }
    });
}
