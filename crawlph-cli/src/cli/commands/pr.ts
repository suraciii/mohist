import { Command } from 'commander';
import chalk from 'chalk';
import http from 'http';
import { ApiResponse, PullRequest } from '../../types';

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

function formatPRState(state: string): string {
  const colors: Record<string, typeof chalk.green> = {
    open: chalk.green,
    approved: chalk.blue,
    'changes-requested': chalk.red,
    merged: chalk.magenta
  };
  
  const color = colors[state] || chalk.white;
  return color(state);
}

export function setupPRCommands(program: Command): void {
  const pr = program.command('pr').description('Manage pull requests');

  pr
    .command('list')
    .description('List pull requests')
    .action(async () => {
      try {
        const response = await apiClient<ApiResponse<PullRequest[]>>('GET', '/prs');
        
        if (response.success && response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow('No pull requests found'));
            return;
          }
          
          console.log(chalk.bold('\nPull Requests:\n'));
          console.log('  #   State              Issue   Title');
          console.log('  ' + '─'.repeat(70));
          
          response.data.forEach((pr) => {
            const num = chalk.cyan(`#${pr.number}`.padEnd(4));
            const state = formatPRState(pr.state).padEnd(18);
            const issue = pr.issueNumber ? chalk.yellow(`#${pr.issueNumber}`) : chalk.gray('N/A');
            const title = pr.title.substring(0, 40);
            
            console.log(`  ${num} ${state} ${issue.padEnd(7)} ${title}`);
          });
          console.log();
        }
      } catch (error) {
        console.error(chalk.red(`Failed to list PRs: ${error}`));
      }
    });

  pr
    .command('show <number>')
    .description('Show pull request details')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<PullRequest>>(
          'GET',
          `/prs/${number}`
        );
        
        if (response.success && response.data) {
          const pr = response.data;
          console.log(chalk.bold(`\nPull Request #${pr.number}: ${pr.title}\n`));
          console.log(`  State: ${formatPRState(pr.state)}`);
          console.log(`  URL: ${chalk.gray(pr.url)}`);
          
          if (pr.issueNumber) {
            console.log(`  Issue: ${chalk.yellow(`#${pr.issueNumber}`)}`);
          }
          
          console.log();
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show PR: ${error}`));
      }
    });

  pr
    .command('review <number>')
    .description('Open pull request in browser')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<PullRequest>>(
          'GET',
          `/prs/${number}`
        );
        
        if (response.success && response.data) {
          const { spawn } = await import('child_process');
          const url = response.data.url;
          
          const platform = process.platform;
          let command: string;
          
          if (platform === 'darwin') {
            command = 'open';
          } else if (platform === 'win32') {
            command = 'start';
          } else {
            command = 'xdg-open';
          }
          
          spawn(command, [url], { detached: true, stdio: 'ignore' }).unref();
          
          console.log(chalk.green(`✓ Opening PR #${number} in browser`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to open PR: ${error}`));
      }
    });

  pr
    .command('approve <number>')
    .description('Approve pull request')
    .option('-m, --message <message>', 'Approval message')
    .action(async (number, options) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/prs/${number}/approve`,
          { message: options.message }
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Approved PR #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to approve PR: ${error}`));
      }
    });

  pr
    .command('request-changes <number> <message>')
    .description('Request changes on pull request')
    .action(async (number, message) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/prs/${number}/request-changes`,
          { message }
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Requested changes on PR #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to request changes: ${error}`));
      }
    });
}
