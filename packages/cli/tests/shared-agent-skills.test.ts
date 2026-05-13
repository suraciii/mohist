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
    it('installs issue-templates.md alongside SKILL.md for mohist', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const mohistDir = path.join(tmpDir, '.agents', 'skills', 'mohist');
      const skillMdPath = path.join(mohistDir, 'SKILL.md');
      const issueTemplatesPath = path.join(mohistDir, 'issue-templates.md');

      expect(fs.existsSync(skillMdPath)).toBe(true);
      expect(fs.existsSync(issueTemplatesPath)).toBe(true);

      const content = fs.readFileSync(issueTemplatesPath, 'utf-8');
      expect(content).toContain('## Template: refactor');
      expect(content).toContain('## Template: user-story');
      expect(content).toContain('## Template: ui');
    });

    it('does not install issue-templates.md for mohist-explore', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const exploreDir = path.join(tmpDir, '.agents', 'skills', 'mohist-explore');
      const skillMdPath = path.join(exploreDir, 'SKILL.md');
      const issueTemplatesPath = path.join(exploreDir, 'issue-templates.md');

      expect(fs.existsSync(skillMdPath)).toBe(true);
      expect(fs.existsSync(issueTemplatesPath)).toBe(false);
    });

    it('installed issue-templates.md contains UI template with ASCII prototype section', () => {
      installSharedAgentSkills({ projectPath: tmpDir });

      const issueTemplatesPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'issue-templates.md');
      const content = fs.readFileSync(issueTemplatesPath, 'utf-8');

      expect(content).toContain('## Template: ui');
      expect(content).toContain('ASCII 原型图');
      expect(content).toContain('+------------------------------------------+');
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

      const userStory = templates.find(t => t.template === 'user-story');
      const refactor = templates.find(t => t.template === 'refactor');
      const ui = templates.find(t => t.template === 'ui');

      expect(userStory).toBeDefined();
      expect(userStory?.labels).toContain('bug');
      expect(userStory?.labels).toContain('feature');
      expect(userStory?.labels).toContain('improvement');

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
      expect(result?.content).toContain('## 重构目标');
      expect(result?.content).toContain('## 当前状态');
      expect(result?.content).toContain('## 验收标准');
    });

    it('returns UI template for ui-feature label', async () => {
      const { getTemplateContent } = await import('../src/agent-skills/issue-template-lookup');
      const result = getTemplateContent('ui-feature');

      expect(result).not.toBeNull();
      expect(result?.template).toBe('ui');
      expect(result?.content).toContain('## ASCII 原型图');
      expect(result?.content).toContain('+------------------------------------------+');
      expect(result?.content).toContain('### 盒子布局示例');
    });

    it('returns UI template for ui-improvement label', async () => {
      const { getTemplateContent } = await import('../src/agent-skills/issue-template-lookup');
      const result = getTemplateContent('ui-improvement');

      expect(result).not.toBeNull();
      expect(result?.template).toBe('ui');
      expect(result?.content).toContain('## ASCII 原型图');
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