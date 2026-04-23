import { Command } from 'commander';
import chalk from 'chalk';
import { execSync, spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { ApiResponse, Issue } from '../../types';
import { slugify } from '../../utils/slugify';
import { apiClient } from '../api-client';
import { requireServer } from '../server-check';

function getDefaultBranch(projectPath: string): string {
  try {
    const symbolicRef = execSync('git symbolic-ref refs/remotes/origin/HEAD', {
      cwd: projectPath,
      stdio: 'pipe',
      encoding: 'utf-8',
    }).trim();
    return symbolicRef.replace('refs/remotes/origin/', '');
  } catch {
    try {
      return execSync('git rev-parse --abbrev-ref HEAD', {
        cwd: projectPath,
        stdio: 'pipe',
        encoding: 'utf-8',
      }).trim();
    } catch {
      return 'main';
    }
  }
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
    .action(async (title, options) => {
      try {
        const response = await apiClient<ApiResponse<Issue>>(
          'POST',
          '/issues',
          { title, body: options.body, labels: options.label }
        );
        
        if (response.success && response.data) {
          console.log(chalk.green(`✓ Created issue #${response.data.number}: ${response.data.title}`));
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
        if (params.length > 0) {
          path += `?${params.join('&')}`;
        }
        
        const response = await apiClient<ApiResponse<Issue[]>>('GET', path);
        
        if (response.success && response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow('No issues found'));
            return;
          }
          
          console.log(chalk.bold('\nIssues:\n'));
          console.log('  ID                 Stage                     Status    Labels              Title');
          console.log('  ' + '─'.repeat(95));
          
          response.data.forEach((issue: any) => {
            const id = chalk.cyan(`${issue.projectName || 'unknown'}#${issue.number}`.padEnd(18));
            const stage = formatStage(issue.stage).padEnd(25);
            const status = formatStatus(issue.status).padEnd(9);
            const labels = formatLabels(issue.labels).padEnd(20);
            const title = issue.title.substring(0, 40);
            
            console.log(`  ${id} ${stage} ${status} ${labels} ${title}`);
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
          console.log(`  Stage: ${formatStage(issue.stage)}`);
          console.log(`  Status: ${formatStatus(issue.status)}`);
          
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
              console.log(`    ${chalk.gray(new Date(comment.createdAt).toLocaleString())}`);
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
    .action(async (number, options) => {
      try {
        const { add, remove } = parseLabelFlags(options.label);
        
        const response = await apiClient<ApiResponse<Issue>>(
          'PATCH',
          `/issues/${number}`,
          {
            title: options.title,
            body: options.body,
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
    .command('reopen <number>')
    .description('Reopen a closed issue')
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
    .description('Approve an issue at an approval gate')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/approve`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Issue #${number} approved, agent resumed`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to approve issue: ${error}`));
      }
    });

  issue
    .command('reject <number>')
    .description('Reject an issue at an approval gate')
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
        const defaultBranch = getDefaultBranch(projectPath);

        try {
          execSync(`git diff ${defaultBranch}...${branchName}`, {
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
        const projectName = issue.projectName;
        if (!projectName || projectName === 'unknown') {
          console.error(chalk.red('Error: No project name found'));
          return;
        }

        const slug = slugify(projectName);

        const home = process.env.HOME || '';
        const logDir = path.join(home, '.mohist', 'projects', slug, 'logs', `issue-${number}`);

        if (!fs.existsSync(logDir)) {
          console.error(chalk.red(`No logs found for issue #${number}`));
          return;
        }

        const logFiles = fs.readdirSync(logDir)
          .filter(f => f.endsWith('.log'))
          .sort();

        if (logFiles.length === 0) {
          console.error(chalk.red(`No logs found for issue #${number}`));
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
