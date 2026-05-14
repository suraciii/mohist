import { Command } from 'commander';
import chalk from 'chalk';
import {
  installSharedAgentSkills,
} from '../../agent-skills/shared-agent-skills';
import { SkillDataService } from '../../agent-skills/skill-data-service';

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
  const skillService = new SkillDataService();

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
    .option('--json', 'Output as JSON')
    .action(async (options) => {
      const allSkills = skillService.discoverSkills();
      const visibleSkills = allSkills.filter(s => !s.hidden);

      if (options.json) {
        const output = visibleSkills.map(s => ({
          name: s.name,
          description: s.description,
          hidden: s.hidden,
          path: s.path,
          stub: s.stub,
        }));
        console.log(JSON.stringify(output, null, 2));
      } else {
        console.log(chalk.bold('\nShared coder agent skills:'));
        for (const skill of visibleSkills) {
          console.log(`  ${chalk.cyan(skill.name)}`);
        }
        console.log(chalk.gray('\n  Install with: mo skills install [--claude]'));
        console.log(chalk.gray('  Get full content: mo skills get <name> [--full]'));
      }
    });

  skills
    .command('get [name]')
    .description('Print built-in skill content. Use --all to print all built-in skills')
    .option('--full', 'Include supplementary files from references/ and templates/')
    .option('--json', 'Output as JSON')
    .option('--all', 'Print all built-in skills')
    .action(async (name, options) => {
      if (options.all) {
        const allSkills = skillService.discoverSkills();
        const visibleSkills = allSkills.filter(s => !s.hidden);
        if (options.json) {
          const output = visibleSkills.map(s => {
            const content = skillService.getSkillContent(s.name, false);
            return {
              name: s.name,
              description: s.description,
              content: content.content,
              path: content.path,
            };
          });
          console.log(JSON.stringify(output, null, 2));
        } else {
          for (const skill of visibleSkills) {
            const content = skillService.getSkillContent(skill.name, false);
            console.log(chalk.bold(`\n# ${skill.name}\n`));
            console.log(content.content);
          }
        }
      } else if (!name) {
        console.error(chalk.red('Error: Skill name required. Use --all to list all skills.'));
        process.exit(1);
      } else {
        try {
          const content = skillService.getSkillContent(name, options.full ?? false);
          if (options.json) {
            const output: Record<string, unknown> = {
              name: content.name,
              content: content.content,
              path: content.path,
            };
            if (options.full && content.supplementaryFiles.length > 0) {
              output.supplementaryFiles = content.supplementaryFiles.map(f => ({
                path: f.path,
                content: f.content,
              }));
            }
            console.log(JSON.stringify(output, null, 2));
          } else {
            console.log(content.content);
            if (options.full) {
              for (const sf of content.supplementaryFiles) {
                console.log(`\n--- ${sf.path} ---\n\n${sf.content}`);
              }
            }
          }
        } catch (err) {
          console.error(chalk.red(`Error: ${err instanceof Error ? err.message : String(err)}`));
          process.exit(1);
        }
      }
    });

  skills
    .command('path <name>')
    .description('Print the packaged directory path for a built-in skill')
    .option('--json', 'Output as JSON')
    .action(async (name, options) => {
      const skillPath = skillService.resolveSkillPath(name);
      if (!skillPath) {
        console.error(chalk.red(`Skill not found: ${name}`));
        process.exit(1);
      }
      if (options.json) {
        console.log(JSON.stringify({ name, path: skillPath }, null, 2));
      } else {
        console.log(skillPath);
      }
    });
}