import { Command } from 'commander';
import chalk from 'chalk';
import {
  installSharedAgentSkills,
  updateSharedAgentSkills,
  getSharedSkillNames,
} from '../../agent-skills/shared-agent-skills';

function formatResult(results: { skill: string; result: string; reason?: string }[]): void {
  for (const r of results) {
    const icon = r.result === 'created' ? '✓' :
                 r.result === 'updated' ? '↻' :
                 r.result === 'unchanged' ? '○' :
                 r.result === 'overwritten' ? '⇄' : '✗';
    const colorFn = r.result === 'skipped-protected' ? chalk.yellow :
                    r.result === 'created' ? chalk.green :
                    r.result === 'updated' ? chalk.cyan :
                    r.result === 'overwritten' ? chalk.magenta :
                    chalk.gray;
    const label = colorFn(`${icon} ${r.skill}`);
    const reason = r.reason ? chalk.yellow(` (${r.reason})`) : '';
    console.log(`  ${label}${reason}`);
  }
}

export function setupSkillsCommands(program: Command): void {
  const skills = program
    .command('skills')
    .description('Manage shared coder agent skills in .agents/skills (not Mohist internal skills in .mohist/skills)');

  skills
    .command('install')
    .description('Install shared coder agent skills (mohist, mohist-explore) into .agents/skills')
    .option('--force', 'Overwrite existing user-edited skill files')
    .option('--path <repo>', 'Target repository path (defaults to current working directory)')
    .action(async (options) => {
      const results = installSharedAgentSkills({
        projectPath: options.path,
        force: options.force,
      });

      const protectedCount = results.filter(r => r.result === 'skipped-protected').length;

      console.log(chalk.bold('\nShared agent skills installed:'));
      formatResult(results);

      if (protectedCount > 0 && !options.force) {
        console.log(chalk.yellow('\n  Use --force to overwrite protected files'));
        process.exit(1);
      }
    });

  skills
    .command('update')
    .description('Update shared coder agent skills in .agents/skills (repairs missing skills, skips protected files)')
    .option('--path <repo>', 'Target repository path (defaults to current working directory)')
    .action(async (options) => {
      const results = updateSharedAgentSkills({
        projectPath: options.path,
      });

      console.log(chalk.bold('\nShared agent skills updated:'));
      formatResult(results);
    });

  skills
    .command('list')
    .description('List shared coder agent skills managed by Mohist')
    .action(async () => {
      const names = getSharedSkillNames();
      console.log(chalk.bold('\nShared coder agent skills:'));
      for (const name of names) {
        console.log(`  ${chalk.cyan(name)}`);
      }
      console.log(chalk.gray('\n  These are installed to .agents/skills, not .mohist/skills'));
    });
}