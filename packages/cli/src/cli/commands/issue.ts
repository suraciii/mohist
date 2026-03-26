import { Command } from 'commander';
import chalk from 'chalk';
import http from 'http';
import { ApiResponse, Issue } from '../../types';

const API_BASE = 'http://localhost:3456/api';

function apiClient<T = any>(
  method: string,
  path: string,
  body?: any
): Promise<T> {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : undefined;
    
    const req = http.request(
      `${API_BASE}${path}`,
      {
        method,
        headers: {
          'Content-Type': 'application/json',
          'Content-Length': data ? Buffer.byteLength(data) : 0
        }
      },
      (res) => {
        let responseData = '';
        
        res.on('data', (chunk) => {
          responseData += chunk;
        });
        
        res.on('end', () => {
          try {
            const parsed = JSON.parse(responseData);
            resolve(parsed);
          } catch (error) {
            reject(new Error('Invalid JSON response'));
          }
        });
      }
    );
    
    req.on('error', reject);
    
    if (data) {
      req.write(data);
    }
    
    req.end();
  });
}

function formatStage(stage: string): string {
  const colors: Record<string, typeof chalk.green> = {
    draft: chalk.gray,
    designing: chalk.blue,
    'waiting-design-review': chalk.yellow,
    implementing: chalk.cyan,
    'waiting-review': chalk.magenta,
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
          
          if (issue.progress) {
            console.log(`\n  ${chalk.gray('Progress:')}`);
            console.log(`  ${issue.progress.current}/${issue.progress.total} (${issue.progress.percentage}%)`);
          }
          
          if (issue.stageInfo) {
            console.log(`\n  ${chalk.gray('Stage Info:')}`);
            console.log(`  ${issue.stageInfo.description}`);
            if (issue.stageInfo.requiresApproval) {
              console.log(`  ${chalk.yellow('⚠ Requires your approval to continue')}`);
            }
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
    .description('Approve the current stage of an issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/approve`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Approved issue #${number}`));
          if (response.data?.message) {
            console.log(chalk.gray(`  ${response.data.message}`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to approve issue: ${error}`));
      }
    });

  issue
    .command('pause <number>')
    .description('Pause issue processing')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/pause`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Paused issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to pause issue: ${error}`));
      }
    });

  issue
    .command('resume <number>')
    .description('Resume issue processing')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/resume`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Resumed issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to resume issue: ${error}`));
      }
    });
}

export function setupLabelCommands(program: Command): void {
  const label = program.command('label').description('Manage labels');

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
