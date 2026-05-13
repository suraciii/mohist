import { Command } from 'commander';
import chalk from 'chalk';
import { getTemplateContent, getAvailableTemplates } from '../../agent-skills/issue-template-lookup';

export function setupInstructionsCommand(program: Command): void {
  const instructions = program
    .command('instructions [label]')
    .description('List available issue templates or print a specific template by label')
    .action((label?: string) => {
      if (!label) {
        listTemplates();
      } else {
        const success = printTemplateForLabel(label);
        if (!success) {
          process.exit(1);
        }
      }
    });

  instructions
    .command('list')
    .description('List available template groups and their labels')
    .action(() => {
      listTemplates();
    });
}

function listTemplates(): void {
  const templates = getAvailableTemplates();
  console.log(chalk.bold('\nAvailable Issue Templates:\n'));
  for (const { template, labels } of templates) {
    console.log(`  ${chalk.cyan(template)} ${chalk.gray('→')} ${labels.map(l => chalk.blue(`[${l}]`)).join(' ')}`);
  }
  console.log(chalk.gray('\n  Use: mo instructions <label>'));
  console.log();
}

function printTemplateForLabel(label: string): boolean {
  const result = getTemplateContent(label);
  if (!result) {
    console.error(chalk.red(`\nError: Unknown label "${label}"\n`));
    console.log(chalk.bold('Valid labels:'));
    const templates = getAvailableTemplates();
    for (const { template, labels } of templates) {
      console.log(`  ${chalk.cyan(template)} ${chalk.gray('→')} ${labels.map(l => chalk.blue(`[${l}]`)).join(' ')}`);
    }
    console.log();
    return false;
  }

  console.log(result.content);
  return true;
}