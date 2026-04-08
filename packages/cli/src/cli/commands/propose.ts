import { Command } from 'commander';
import chalk from 'chalk';
import { ApiResponse } from '../../types';
import { apiClient } from '../api-client';
import { requireServer } from '../server-check';

export function setupProposeCommands(program: Command): void {
  const propose = program
    .command('propose <issue-number>')
    .description('Create a Change for an issue and start the Plan phase')
    .option('--force', 'Force overwrite existing Change (dangerous!)')
    .hook('preAction', async () => {
      await requireServer();
    });

  propose.action(async (issueNumber, options) => {
    try {
      const response = await apiClient<ApiResponse<any>>(
        'POST',
        `/propose/${issueNumber}/propose`,
        { force: options.force }
      );

      if (response.success && response.data) {
        console.log(chalk.green(`✓ ${response.data.message}`));
        if (response.data.changePath) {
          console.log(chalk.gray(`  Change: ${response.data.changeName}`));
          console.log(chalk.gray(`  Path: ${response.data.changePath}`));
        }
      } else {
        console.error(chalk.red(`Error: ${response.error}`));
        process.exit(1);
      }
    } catch (error) {
      console.error(chalk.red(`Failed to propose issue: ${error}`));
      process.exit(1);
    }
  });
}