import { Command } from 'commander';
import chalk from 'chalk';
import {
  installSharedAgentSkills,
  getSharedSkillNames,
} from '../../agent-skills/shared-agent-skills';

const SKILL_TYPE_HELP = `
These commands manage coder agent skills under .agents/skills (OpenCode) or .claude/skills (Claude Code).
They do not execute, scan, or modify Mohist internal skills under .mohist/skills.
`;

function formatResult(results: { skill: string; result: string }[], claude: boolean): void {
  const target = claude ? '.claude/skills' : '.agents/skills';
  for (const r of results) {
    const icon = r.result === 'created' ? '✓' : '↻';
    const colorFn = r.result === 'created' ? chalk.green : chalk.cyan;
    console.log(`  ${colorFn(`${icon} ${r.skill}`)} ${chalk.gray(`→ ${target}/${r.skill}/SKILL.md`)}`);
  }
}

export function setupSkillsCommands(program: Command): void {
  const skills = program
    .command('skills')
    .description('Manage shared coder agent skills for OpenCode (.agents/skills) or Claude Code (.claude/skills)')
    .addHelpText('after', SKILL_TYPE_HELP);

  skills
    .command('install')
    .description('Install shared coder agent skills; use --claude for Claude Code, defaults to OpenCode')
    .option('--path <repo>', 'Target repository path (defaults to current working directory)')
    .option('--claude', 'Install to .claude/skills for Claude Code instead of .agents/skills')
    .addHelpText('after', SKILL_TYPE_HELP)
    .action(async (options) => {
      const results = installSharedAgentSkills({
        projectPath: options.path,
        claude: options.claude ?? false,
      });

      console.log(chalk.bold('\nShared agent skills installed:'));
      formatResult(results, options.claude ?? false);
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
      console.log(chalk.gray('\n  Install with: mo skills install [--claude]'));
    });
}