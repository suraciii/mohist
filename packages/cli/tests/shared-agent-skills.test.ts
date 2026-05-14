import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import {
  installSharedAgentSkills,
  installHermesSkills,
  getSharedSkillNames,
} from '../src/agent-skills/shared-agent-skills';
import { SkillDataService, findSkillDataRootCandidates } from '../src/agent-skills/skill-data-service';
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

    it('--claude writes to .claude/skills instead of .agents/skills', () => {
      const results = installSharedAgentSkills({ projectPath: tmpDir, claude: true });

      const claudeMohistPath = path.join(tmpDir, '.claude', 'skills', 'mohist', 'SKILL.md');
      const claudeExplorePath = path.join(tmpDir, '.claude', 'skills', 'mohist-explore', 'SKILL.md');
      const agentMohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');

      expect(fs.existsSync(claudeMohistPath)).toBe(true);
      expect(fs.existsSync(claudeExplorePath)).toBe(true);
      expect(fs.existsSync(agentMohistPath)).toBe(false);

      expect(results.some(r => r.skill === 'mohist' && r.result === 'created')).toBe(true);
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
      expect(names.length).toBe(2);
    });
  });

  describe('installHermesSkills', () => {
    let tmpHermesHome: string;

    beforeEach(() => {
      tmpHermesHome = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-hermes-test-'));
    });

    afterEach(() => {
      fs.rmSync(tmpHermesHome, { recursive: true, force: true });
    });

    it('installs mohist and mohist-explore to HERMES_HOME/skills', () => {
      const results = installHermesSkills({ hermesHome: tmpHermesHome });

      const mohistPath = path.join(tmpHermesHome, 'skills', 'mohist', 'SKILL.md');
      const explorePath = path.join(tmpHermesHome, 'skills', 'mohist-explore', 'SKILL.md');

      expect(fs.existsSync(mohistPath)).toBe(true);
      expect(fs.existsSync(explorePath)).toBe(true);
      expect(results.some(r => r.skill === 'mohist' && r.result === 'created')).toBe(true);
      expect(results.some(r => r.skill === 'mohist-explore' && r.result === 'created')).toBe(true);
    });

    it('HERMES_HOME env var controls install root', () => {
      const originalHermesHome = process.env.HERMES_HOME;
      process.env.HERMES_HOME = tmpHermesHome;
      try {
        const results = installHermesSkills();
        const mohistPath = path.join(tmpHermesHome, 'skills', 'mohist', 'SKILL.md');
        expect(fs.existsSync(mohistPath)).toBe(true);
        expect(results[0].result).toBe('created');
      } finally {
        if (originalHermesHome !== undefined) {
          process.env.HERMES_HOME = originalHermesHome;
        } else {
          delete process.env.HERMES_HOME;
        }
      }
    });

    it('copies full skill-data recursively including references/issue-templates.md', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const refsPath = path.join(tmpHermesHome, 'skills', 'mohist', 'references', 'issue-templates.md');
      expect(fs.existsSync(refsPath)).toBe(true);
      const refsContent = fs.readFileSync(refsPath, 'utf-8');
      expect(refsContent).toContain('## Template: refactor');
      expect(refsContent).toContain('## Template: product');
      expect(refsContent).toContain('## Template: ui');
    });

    it('installed SKILL.md is full content, not the hidden stub', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const mohistPath = path.join(tmpHermesHome, 'skills', 'mohist', 'SKILL.md');
      const content = fs.readFileSync(mohistPath, 'utf-8');
      expect(content).not.toContain('hidden: true');
      expect(content).not.toContain('获取完整指令');
      expect(content).toContain('mo issue create');
      expect(content.split('\n').length).toBeGreaterThan(50);
    });

    it('mohist-explore installed SKILL.md is not the hidden stub', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const explorePath = path.join(tmpHermesHome, 'skills', 'mohist-explore', 'SKILL.md');
      const content = fs.readFileSync(explorePath, 'utf-8');
      expect(content).not.toContain('hidden: true');
    });

    it('fails when a packaged Hermes skill is missing instead of falling back to stubs', () => {
      const service = new SkillDataService();
      const packagedPath = service.resolvePackagedSkillPath('mohist');
      expect(packagedPath).toBeTruthy();

      const renameTarget = `${packagedPath!}-bak`;
      fs.renameSync(packagedPath!, renameTarget);

      try {
        expect(() => installHermesSkills({ hermesHome: tmpHermesHome })).toThrow(/Packaged Hermes skill not found: mohist/i);

        const installedPath = path.join(tmpHermesHome, 'skills', 'mohist', 'SKILL.md');
        expect(fs.existsSync(installedPath)).toBe(false);
      } finally {
        fs.renameSync(renameTarget, packagedPath!);
      }
    });

    it('does not install mohist-po user skill', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const poPath = path.join(tmpHermesHome, 'skills', 'mohist-po');
      expect(fs.existsSync(poPath)).toBe(false);
    });

    it('first install reports created', () => {
      const results = installHermesSkills({ hermesHome: tmpHermesHome });

      for (const r of results) {
        expect(r.result).toBe('created');
      }
    });

    it('repeated install reports updated and overwrites', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const mohistPath = path.join(tmpHermesHome, 'skills', 'mohist', 'SKILL.md');
      fs.writeFileSync(mohistPath, '# Modified content\n', 'utf-8');

      const results = installHermesSkills({ hermesHome: tmpHermesHome });
      expect(results.some(r => r.skill === 'mohist' && r.result === 'updated')).toBe(true);

      const content = fs.readFileSync(mohistPath, 'utf-8');
      expect(content).not.toBe('# Modified content\n');
    });

    it('installHermesSkills does not write to .agents/skills', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const agentMohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      expect(fs.existsSync(agentMohistPath)).toBe(false);
    });

    it('installHermesSkills does not affect .claude/skills', () => {
      const originalCwd = process.cwd();
      process.chdir(tmpDir);
      try {
        installHermesSkills({ hermesHome: tmpHermesHome });

        const claudeMohistPath = path.join(tmpDir, '.claude', 'skills', 'mohist', 'SKILL.md');
        expect(fs.existsSync(claudeMohistPath)).toBe(false);
      } finally {
        process.chdir(originalCwd);
      }
    });

    it('installHermesSkills leaves unrelated skill directories untouched', () => {
      const unrelatedSkillDir = path.join(tmpHermesHome, 'skills', 'custom-skill');
      fs.mkdirSync(unrelatedSkillDir, { recursive: true });
      fs.writeFileSync(
        path.join(unrelatedSkillDir, 'SKILL.md'),
        '---\nname: custom-skill\ndescription: Custom user skill\n---\nCustom content\n'
      );

      installHermesSkills({ hermesHome: tmpHermesHome });

      const customContent = fs.readFileSync(path.join(unrelatedSkillDir, 'SKILL.md'), 'utf-8');
      expect(customContent).toContain('Custom content');
    });

    it('installHermesSkills respects custom hermesHome not real ~/.hermes', () => {
      const customHome = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-custom-hermes-'));
      try {
        const results = installHermesSkills({ hermesHome: customHome });
        expect(results.length).toBeGreaterThan(0);
        expect(fs.existsSync(path.join(customHome, 'skills', 'mohist', 'SKILL.md'))).toBe(true);
      } finally {
        fs.rmSync(customHome, { recursive: true, force: true });
      }
    });

    it('built SkillDataService prefers dist packaged assets over source fallback', () => {
      const distRoot = path.join(tmpDir, 'dist', 'agent-skills');
      const srcRoot = path.join(tmpDir, 'src', 'agent-skills');
      for (const root of [distRoot, srcRoot]) {
        fs.mkdirSync(path.join(root, 'skill-data', 'mohist'), { recursive: true });
        fs.mkdirSync(path.join(root, 'skill-data', 'mohist-explore'), { recursive: true });
      }
      fs.writeFileSync(path.join(distRoot, 'skill-data', 'mohist', 'SKILL.md'), 'dist mohist', 'utf-8');
      fs.writeFileSync(path.join(distRoot, 'skill-data', 'mohist-explore', 'SKILL.md'), 'dist explore', 'utf-8');
      fs.writeFileSync(path.join(srcRoot, 'skill-data', 'mohist', 'SKILL.md'), 'src mohist', 'utf-8');
      fs.writeFileSync(path.join(srcRoot, 'skill-data', 'mohist-explore', 'SKILL.md'), 'src explore', 'utf-8');

      const selectedRoot = findSkillDataRootCandidates(distRoot).find(root => fs.existsSync(root));

      expect(selectedRoot).toBe(distRoot);
      expect(path.join(selectedRoot!, 'skill-data', 'mohist')).toBe(path.join(distRoot, 'skill-data', 'mohist'));
    });
  });

  describe('installSharedAgentSkills vs installHermesSkills separation', () => {
    let tmpHermesHome: string;

    beforeEach(() => {
      tmpHermesHome = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-hermes-separate-test-'));
    });

    afterEach(() => {
      fs.rmSync(tmpHermesHome, { recursive: true, force: true });
    });

    it('--claude writes to .claude/skills, not Hermes skills', () => {
      const originalCwd = process.cwd();
      process.chdir(tmpDir);
      try {
        const results = installSharedAgentSkills({ projectPath: tmpDir, claude: true });
        expect(results.some(r => r.skill === 'mohist' && r.result === 'created')).toBe(true);

        const claudeMohistPath = path.join(tmpDir, '.claude', 'skills', 'mohist', 'SKILL.md');
        expect(fs.existsSync(claudeMohistPath)).toBe(true);

        const hermesMohistPath = path.join(tmpHermesHome, 'skills', 'mohist', 'SKILL.md');
        expect(fs.existsSync(hermesMohistPath)).toBe(false);
      } finally {
        process.chdir(originalCwd);
      }
    });

    it('installHermesSkills does not create .agents directory', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const agentDir = path.join(tmpDir, '.agents');
      expect(fs.existsSync(agentDir)).toBe(false);
    });

    it('installHermesSkills does not create .claude directory', () => {
      installHermesSkills({ hermesHome: tmpHermesHome });

      const claudeDir = path.join(tmpDir, '.claude');
      expect(fs.existsSync(claudeDir)).toBe(false);
    });
  });

  describe('CLI incompatible options', () => {
    it('setupSkillsCommands registers --hermes option', () => {
      const program = new Command();
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      const installCmd = skillsCmd?.commands.find(cmd => cmd.name() === 'install');

      expect(installCmd?.options.some(opt => opt.long === '--hermes')).toBe(true);
    });

    it('--hermes and --claude cannot be used together', async () => {
      const errors: string[] = [];
      const originalError = console.error;
      console.error = (msg: string) => errors.push(msg);

      try {
        const program = new Command();
        setupSkillsCommands(program);
        const installCmd = program.commands.find(cmd => cmd.name() === 'skills')?.commands.find(cmd => cmd.name() === 'install');

        let exitCode = 0;
        try {
          await installCmd?.parseAsync(['node', 'test', '--hermes', '--claude'], { from: 'user' });
        } catch {
          exitCode = 1;
        }
        expect(exitCode).toBe(1);
        expect(errors.some(e => e.includes('--hermes') && e.includes('--claude'))).toBe(true);
      } finally {
        console.error = originalError;
      }
    });

    it('--hermes and --path cannot be used together', async () => {
      const errors: string[] = [];
      const originalError = console.error;
      console.error = (msg: string) => errors.push(msg);

      try {
        const program = new Command();
        setupSkillsCommands(program);
        const installCmd = program.commands.find(cmd => cmd.name() === 'skills')?.commands.find(cmd => cmd.name() === 'install');

        let exitCode = 0;
        try {
          await installCmd?.parseAsync(['node', 'test', '--hermes', '--path', '/some/path'], { from: 'user' });
        } catch {
          exitCode = 1;
        }
        expect(exitCode).toBe(1);
        expect(errors.some(e => e.includes('--hermes') && e.includes('--path'))).toBe(true);
      } finally {
        console.error = originalError;
      }
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

    it('skills install command has --path, --claude options', () => {
      const program = new Command();
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      const installCmd = skillsCmd?.commands.find(cmd => cmd.name() === 'install');

      expect(installCmd?.options.some(opt => opt.long === '--force')).toBe(false);
      expect(installCmd?.options.some(opt => opt.long === '--path')).toBe(true);
      expect(installCmd?.options.some(opt => opt.long === '--claude')).toBe(true);
    });

    it('help mentions both .agents/skills and .claude/skills', () => {
      const program = new Command();
      program.name('mo');
      setupSkillsCommands(program);

      const skillsCmd = program.commands.find(cmd => cmd.name() === 'skills');
      const installCmd = skillsCmd?.commands.find(cmd => cmd.name() === 'install');

      const installHelp = installCmd?.helpInformation() ?? '';
      expect(installHelp).toContain('.agents/skills');
      expect(installHelp).toContain('.claude/skills');
      expect(installHelp).toContain('--claude');
    });
  });

  describe('issue-templates.md bundle installation', () => {
    it('mo skills get mohist --full serves issue-templates.md as supplementary file', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const skillService = new SkillDataService();
      const content = skillService.getSkillContent('mohist', true);
      const hasRefs = content.supplementaryFiles.some(f => f.path.includes('issue-templates'));
      expect(hasRefs).toBe(true);
      const refsFile = content.supplementaryFiles.find(f => f.path.includes('issue-templates'));
      expect(refsFile!.content).toContain('## Template: refactor');
      expect(refsFile!.content).toContain('## Template: product');
      expect(refsFile!.content).toContain('## Template: ui');
    });

    it('issue-templates.md is NOT installed in repository skill directory (served via get --full)', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const mohistDir = path.join(tmpDir, '.agents', 'skills', 'mohist');
      const issueTemplatesPath = path.join(mohistDir, 'issue-templates.md');
      expect(fs.existsSync(issueTemplatesPath)).toBe(false);
    });

    it('does not install issue-templates.md for mohist-explore', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const exploreDir = path.join(tmpDir, '.agents', 'skills', 'mohist-explore');
      const skillMdPath = path.join(exploreDir, 'SKILL.md');
      const issueTemplatesPath = path.join(exploreDir, 'issue-templates.md');
      expect(fs.existsSync(skillMdPath)).toBe(true);
      expect(fs.existsSync(issueTemplatesPath)).toBe(false);
    });

    it('installed SKILL.md for mohist is a stub under 50 lines', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const content = fs.readFileSync(mohistPath, 'utf-8');
      const lines = content.split('\n').length;
      expect(lines).toBeLessThan(50);
      expect(content).toContain('hidden: true');
    });

    it('getSharedSkillNames returns only skill names, not companion files', () => {
      const names = getSharedSkillNames();
      expect(names).toContain('mohist');
      expect(names).toContain('mohist-explore');
      expect(names).not.toContain('issue-templates.md');
    });
  });
});

describe('Issue Template Instructions', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-instructions-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('mo instructions command setup', () => {
    it('setupInstructionsCommand registers instructions command', async () => {
      vi.mock('../src/cli/api-client', () => ({ apiClient: vi.fn() }));
      vi.mock('../src/cli/server-check', () => ({ requireServer: vi.fn().mockResolvedValue(undefined) }));

      const { setupInstructionsCommand } = await import('../src/cli/commands/instructions');
      const program = new Command();
      setupInstructionsCommand(program);

      const instructionsCmd = program.commands.find(cmd => cmd.name() === 'instructions');
      expect(instructionsCmd).toBeDefined();
      expect(instructionsCmd?.commands.some(cmd => cmd.name() === 'list')).toBe(true);
    });
  });

  describe('getAvailableTemplates', () => {
    it('returns all template groups with their labels', async () => {
      const { getAvailableTemplates } = await import('../src/agent-skills/issue-template-lookup');
      const templates = getAvailableTemplates();

      const product = templates.find(t => t.template === 'product');
      const refactor = templates.find(t => t.template === 'refactor');
      const ui = templates.find(t => t.template === 'ui');

      expect(product).toBeDefined();
      expect(product?.labels).toContain('bug');
      expect(product?.labels).toContain('feature');
      expect(product?.labels).toContain('improvement');

      expect(refactor).toBeDefined();
      expect(refactor?.labels).toContain('refactor');

      expect(ui).toBeDefined();
      expect(ui?.labels).toContain('ui-feature');
      expect(ui?.labels).toContain('ui-improvement');
    });
  });

  describe('getTemplateContent', () => {
    it('returns refactor template for refactor label', async () => {
      const { getTemplateContent } = await import('../src/agent-skills/issue-template-lookup');
      const result = getTemplateContent('refactor');

      expect(result).not.toBeNull();
      expect(result?.template).toBe('refactor');
      expect(result?.content).toContain('## Refactor Goal');
      expect(result?.content).toContain('## Refactor Shape');
      expect(result?.content).toContain('## Acceptance Criteria');
    });

    it('returns UI template for ui-feature label', async () => {
      const { getTemplateContent } = await import('../src/agent-skills/issue-template-lookup');
      const result = getTemplateContent('ui-feature');

      expect(result).not.toBeNull();
      expect(result?.template).toBe('ui');
      expect(result?.content).toContain('## Product Shape');
      expect(result?.content).toContain('+------------------------------------------+');
      expect(result?.content).toContain('## Acceptance Criteria');
    });

    it('returns UI template for ui-improvement label', async () => {
      const { getTemplateContent } = await import('../src/agent-skills/issue-template-lookup');
      const result = getTemplateContent('ui-improvement');

      expect(result).not.toBeNull();
      expect(result?.template).toBe('ui');
      expect(result?.content).toContain('## Product Shape');
    });

    it('returns null for unknown label', async () => {
      const { getTemplateContent } = await import('../src/agent-skills/issue-template-lookup');
      const result = getTemplateContent('unknown-label');

      expect(result).toBeNull();
    });

    it('normalizes label by lowercasing and trimming', async () => {
      const { getTemplateContent } = await import('../src/agent-skills/issue-template-lookup');
      const result = getTemplateContent('  REFACTOR  ');

      expect(result).not.toBeNull();
      expect(result?.template).toBe('refactor');
    });
  });
});
