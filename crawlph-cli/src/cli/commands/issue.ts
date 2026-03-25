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

export function setupIssueCommands(program: Command): void {
  const issue = program.command('issue').description('Manage issues');

  issue
    .command('create <title>')
    .description('Create a new issue')
    .option('-b, --body <body>', 'Issue body/description')
    .action(async (title, options) => {
      try {
        const response = await apiClient<ApiResponse<Issue>>(
          'POST',
          '/issues',
          { title, body: options.body }
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
    .action(async (options) => {
      try {
        let path = '/issues';
        if (options.status) {
          path += `?stage=${options.status}`;
        }
        
        const response = await apiClient<ApiResponse<Issue[]>>('GET', path);
        
        if (response.success && response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow('No issues found'));
            return;
          }
          
          console.log(chalk.bold('\nIssues:\n'));
          console.log('  #   Stage                     Status    Title');
          console.log('  ' + '─'.repeat(70));
          
          response.data.forEach((issue) => {
            const num = chalk.cyan(`#${issue.number}`.padEnd(4));
            const stage = formatStage(issue.stage).padEnd(25);
            const status = formatStatus(issue.status).padEnd(9);
            const title = issue.title.substring(0, 40);
            
            console.log(`  ${num} ${stage} ${status} ${title}`);
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
          console.log(chalk.bold(`\nIssue #${issue.number}: ${issue.title}\n`));
          console.log(`  Stage: ${formatStage(issue.stage)}`);
          console.log(`  Status: ${formatStatus(issue.status)}`);
          
          if (issue.body) {
            console.log(`\n  ${chalk.gray('Body:')}`);
            console.log(`  ${issue.body.split('\n').join('\n  ')}`);
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
