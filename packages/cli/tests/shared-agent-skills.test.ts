import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import {
  installSharedAgentSkills,
  getSharedSkillNames,
} from '../src/agent-skills/shared-agent-skills';
import { setupSkillsCommands } from '../src/cli/commands/skills';
import { Command } from 'commander';

describe('Shared Agent Skills', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-skills-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('installSharedAgentSkills', () => {
    it('creates mohist and mohist-explore SKILL.md files under .agents/skills', () => {
      const results = installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const explorePath = path.join(tmpDir, '.agents', 'skills', 'mohist-explore', 'SKILL.md');

      expect(fs.existsSync(mohistPath)).toBe(true);
      expect(fs.existsSync(explorePath)).toBe(true);

      expect(results.some(r => r.skill === 'mohist' && r.result === 'created')).toBe(true);
      expect(results.some(r => r.skill === 'mohist-explore' && r.result === 'created')).toBe(true);
    });

    it('does not generate mohist-walkthrough', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const walkthroughPath = path.join(tmpDir, '.agents', 'skills', 'mohist-walkthrough', 'SKILL.md');
      expect(fs.existsSync(walkthroughPath)).toBe(false);
    });

    it('generated frontmatter contains name and description and name equals directory name', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const content = fs.readFileSync(mohistPath, 'utf-8');

      const nameMatch = content.match(/^name:\s*(.+)$/m);
      const descMatch = content.match(/^description:\s*(.+)$/m);

      expect(nameMatch).toBeTruthy();
      expect(descMatch).toBeTruthy();
      expect(nameMatch[1].trim()).toBe('mohist');

      const explorePath = path.join(tmpDir, '.agents', 'skills', 'mohist-explore', 'SKILL.md');
      const exploreContent = fs.readFileSync(explorePath, 'utf-8');
      const exploreNameMatch = exploreContent.match(/^name:\s*(.+)$/m);
      expect(exploreNameMatch[1].trim()).toBe('mohist-explore');
    });

    it('generated content starts with AgentSkills frontmatter', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      for (const skillName of getSharedSkillNames()) {
        const skillPath = path.join(tmpDir, '.agents', 'skills', skillName, 'SKILL.md');
        const content = fs.readFileSync(skillPath, 'utf-8');
        const frontmatterMatch = content.match(/^---\n([\s\S]*?)\n---\n/);

        expect(content.startsWith('---\n')).toBe(true);
        expect(frontmatterMatch).toBeTruthy();
        expect(frontmatterMatch?.[1].match(/^name:\s*(.+)$/m)?.[1].trim()).toBe(skillName);
      }
    });

    it('repeated install overwrites and reports updated', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      fs.writeFileSync(mohistPath, '# Modified content\n', 'utf-8');

      const results = installSharedAgentSkills({ projectPath: tmpDir });

      const updated = results.filter(r => r.result === 'updated');
      expect(updated.length).toBeGreaterThan(0);

      const content = fs.readFileSync(mohistPath, 'utf-8');
      expect(content).not.toBe('# Modified content\n');
    });

    it('--path writes to the target directory and not the process working directory', () => {
      const originalCwd = process.cwd();
      process.chdir(tmpDir);

      try {
        const otherDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-skills-target-'));

        installSharedAgentSkills({ projectPath: otherDir });

        const mohistPath = path.join(otherDir, '.agents', 'skills', 'mohist', 'SKILL.md');
        expect(fs.existsSync(mohistPath)).toBe(true);

        const cwdMohistPath = path.join(process.cwd(), '.agents', 'skills', 'mohist', 'SKILL.md');
        expect(fs.existsSync(cwdMohistPath)).toBe(false);

        fs.rmSync(otherDir, { recursive: true, force: true });
      } finally {
        process.chdir(originalCwd);
      }
    });
  });

  describe('getSharedSkillNames', () => {
    it('returns only mohist and mohist-explore', () => {
      const names = getSharedSkillNames();
      expect(names).toContain('mohist');
      expect(names).toContain('mohist-explore');
      expect(names).not.toContain('mohist-walkthrough');
      expect(names.length).toBe(2);
    });
  });

  describe('CLI command setup', () => {
    it('setupSkillsCommands registers skills install list commands', () => {
      const program = new Command();
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      expect(skillsCmd).toBeDefined();

      expect(skillsCmd?.commands.some(cmd => cmd.name() === 'install')).toBe(true);
      expect(skillsCmd?.commands.some(cmd => cmd.name() === 'update')).toBe(false);
      expect(skillsCmd?.commands.some(cmd => cmd.name() === 'list')).toBe(true);
    });

    it('skills install command has --path option but not --force', () => {
      const program = new Command();
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      const installCmd = skillsCmd?.commands.find(cmd => cmd.name() === 'install');

      expect(installCmd?.options.some(opt => opt.long === '--force')).toBe(false);
      expect(installCmd?.options.some(opt => opt.long === '--path')).toBe(true);
    });

    it('help distinguishes coder agent skills from internal Mohist skills for all skills commands', () => {
      const program = new Command();
      program.name('mo');
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      const installCmd = skillsCmd?.commands.find(cmd => cmd.name() === 'install');

      for (const help of [skillsCmd, installCmd].map(cmd => cmd?.helpInformation())) {
        const normalizedHelp = help?.replace(/\s+/g, ' ');
        expect(help).toContain('.agents/skills');
        expect(help).toContain('coder agent skills');
        expect(help).toContain('.mohist/skills');
        expect(help).toContain('do not execute');
        expect(normalizedHelp).toContain('do not execute, scan, or modify Mohist internal skills');
      }
    });
  });
});