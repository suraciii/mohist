import { Command } from 'commander';
import { startServer, stopServer, serverStatus, serverLogs } from './commands/server';
import { setupProjectCommands, setupInitCommand } from './commands/project';
import { setupIssueCommands, setupLabelCommands } from './commands/issue';
import { setupQuickCommands } from './commands/quick';
import { setupAttachCommand } from './commands/attach';
import { setupProvidersCommands } from './commands/providers';
import { setupProposeCommands } from './commands/propose';
export { requireServer, formatError } from './server-check';

const program = new Command();

program
  .name('mo')
  .description('AI-powered issue workflow automation tool')
  .version('0.1.0');

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
setupAttachCommand(program);
setupProvidersCommands(program);
setupProposeCommands(program);

program.parse();
