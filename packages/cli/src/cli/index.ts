import { Command } from 'commander';
import chalk from 'chalk';
import http from 'http';
import { startServer, stopServer, serverStatus, serverLogs } from './commands/server';
import { setupProjectCommands, setupInitCommand } from './commands/project';
import { setupIssueCommands, setupLabelCommands } from './commands/issue';
import { setupQuickCommands } from './commands/quick';

const program = new Command();

program
  .name('mo')
  .description('AI-powered issue workflow automation tool')
  .version('0.1.0');

function checkServerHealth(): Promise<boolean> {
  return new Promise((resolve) => {
    const req = http.get('http://localhost:3456/api/health', (res) => {
      resolve(res.statusCode === 200);
    });
    
    req.on('error', () => resolve(false));
    req.setTimeout(2000, () => {
      req.destroy();
      resolve(false);
    });
  });
}

export async function requireServer(): Promise<void> {
  const isRunning = await checkServerHealth();
  if (!isRunning) {
    console.error(chalk.red('Error: Server is not running'));
    console.error(chalk.yellow('Start the server with: mo server start'));
    process.exit(1);
  }
}

export function formatError(error: string): void {
  console.error(chalk.red(`Error: ${error}`));
}

const serverCmd = program
  .command('server')
  .description('Manage the mohist server');

serverCmd
  .command('start')
  .description('Start the mohist server in daemon mode')
  .action(async () => {
    await startServer();
  });

serverCmd
  .command('stop')
  .description('Stop the mohist server')
  .action(async () => {
    await stopServer();
  });

serverCmd
  .command('status')
  .description('Check server status')
  .action(async () => {
    await serverStatus();
  });

serverCmd
  .command('logs')
  .description('View server logs')
  .option('-n, --lines <number>', 'Number of lines to show', '50')
  .action(async (options) => {
    const lines = parseInt(options.lines, 10) || 50;
    await serverLogs(lines);
  });

setupProjectCommands(program);
setupInitCommand(program);
setupIssueCommands(program);
setupLabelCommands(program);
setupQuickCommands(program);

program.parse();
