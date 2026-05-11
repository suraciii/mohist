import { Command } from 'commander';
import chalk from 'chalk';
import {
  installSharedAgentSkills,
  getSharedSkillNames,
} from '../../agent-skills/shared-agent-skills';

const SKILL_TYPE_HELP = `
These commands manage coder agent skills under .agents/skills.
They do not execute, scan, or modify Mohist internal skills under .mohist/skills.
`;

function formatResult(results: { skill: string; result: string }[]): void {
  for (const r of results) {
    const icon = r.result === 'created' ? '✓' : '↻';
    const colorFn = r.result === 'created' ? chalk.green : chalk.cyan;
    console.log(`  ${colorFn(`${icon} ${r.skill}`)}`);
  }
}

export function setupSkillsCommands(program: Command): void {
  const skills = program
    .command('skills')
    .description('Manage shared coder agent skills in .agents/skills; do not execute, scan, or modify Mohist internal skills in .mohist/skills')
    .addHelpText('after', SKILL_TYPE_HELP);

  skills
    .command('install')
    .description('Install shared coder agent skills into .agents/skills; do not execute, scan, or modify Mohist internal skills in .mohist/skills')
    .option('--path <repo>', 'Target repository path (defaults to current working directory)')
    .addHelpText('after', SKILL_TYPE_HELP)
    .action(async (options) => {
      const results = installSharedAgentSkills({
        projectPath: options.path,
      });

      console.log(chalk.bold('\nShared agent skills installed:'));
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