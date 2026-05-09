import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import {
  installSharedAgentSkills,
  updateSharedAgentSkills,
  getSharedSkillNames,
  SkillOperationResult,
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

    it('repeated install leaves contents unchanged', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const originalContent = fs.readFileSync(mohistPath, 'utf-8');

      const results = installSharedAgentSkills({ projectPath: tmpDir });

      const newContent = fs.readFileSync(mohistPath, 'utf-8');
      expect(newContent).toBe(originalContent);

      const unchangedResults = results.filter(r => r.result === 'unchanged');
      expect(unchangedResults.length).toBeGreaterThan(0);
    });

    it('update recreates a missing distributed skill while preserving existing protected files', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      fs.unlinkSync(mohistPath);

      const explorePath = path.join(tmpDir, '.agents', 'skills', 'mohist-explore', 'SKILL.md');
      const exploreContent = fs.readFileSync(explorePath, 'utf-8');

      const results = updateSharedAgentSkills({ projectPath: tmpDir });

      expect(fs.existsSync(mohistPath)).toBe(true);
      expect(fs.existsSync(explorePath)).toBe(true);

      const currentExploreContent = fs.readFileSync(explorePath, 'utf-8');
      expect(currentExploreContent).toBe(exploreContent);
    });

    it('manually modified skill files are skipped without --force', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      fs.writeFileSync(mohistPath, '# Modified content\n', 'utf-8');

      const results = installSharedAgentSkills({ projectPath: tmpDir });

      const skipped = results.filter(r => r.result === 'skipped-protected');
      expect(skipped.length).toBeGreaterThan(0);

      const content = fs.readFileSync(mohistPath, 'utf-8');
      expect(content).toBe('# Modified content\n');
    });

    it('install --force overwrites protected skill files', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      fs.writeFileSync(mohistPath, '# Modified content\n', 'utf-8');

      const results = installSharedAgentSkills({ projectPath: tmpDir, force: true });

      const overwritten = results.filter(r => r.result === 'overwritten');
      expect(overwritten.length).toBeGreaterThan(0);

      const content = fs.readFileSync(mohistPath, 'utf-8');
      expect(content).toContain('<!-- Generated by Mohist -->');
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

  describe('updateSharedAgentSkills', () => {
    it('repeated update over generated files reports unchanged without duplicate output', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const results1 = updateSharedAgentSkills({ projectPath: tmpDir });
      const unchanged1 = results1.filter(r => r.result === 'unchanged');
      expect(unchanged1.length).toBe(2);

      const results2 = updateSharedAgentSkills({ projectPath: tmpDir });
      const unchanged2 = results2.filter(r => r.result === 'unchanged');
      expect(unchanged2.length).toBe(2);
    });

    it('update creates missing mohist when mohist-explore exists', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      fs.rmSync(mohistPath);

      const results = updateSharedAgentSkills({ projectPath: tmpDir });

      expect(fs.existsSync(mohistPath)).toBe(true);
      expect(results.some(r => r.skill === 'mohist' && r.result === 'created')).toBe(true);
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
    it('setupSkillsCommands registers skills install update list commands', () => {
      const program = new Command();
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      expect(skillsCmd).toBeDefined();

      expect(skillsCmd?.commands.some(cmd => cmd.name() === 'install')).toBe(true);
      expect(skillsCmd?.commands.some(cmd => cmd.name() === 'update')).toBe(true);
      expect(skillsCmd?.commands.some(cmd => cmd.name() === 'list')).toBe(true);
    });

    it('skills install command has --force and --path options', () => {
      const program = new Command();
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      const installCmd = skillsCmd?.commands.find(cmd => cmd.name() === 'install');

      expect(installCmd?.options.some(opt => opt.long === '--force')).toBe(true);
      expect(installCmd?.options.some(opt => opt.long === '--path')).toBe(true);
    });

    it('skills update command has --path option', () => {
      const program = new Command();
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      const updateCmd = skillsCmd?.commands.find(cmd => cmd.name() === 'update');

      expect(updateCmd?.options.some(opt => opt.long === '--path')).toBe(true);
    });
  });
});