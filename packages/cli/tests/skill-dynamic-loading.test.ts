import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { SkillDataService } from '../src/agent-skills/skill-data-service';
import { installSharedAgentSkills, getSharedSkillNames } from '../src/agent-skills/shared-agent-skills';

const skillDataRoot = path.join(__dirname, '../src/agent-skills');
const stubsRoot = path.join(skillDataRoot, 'stubs');
const skillDataMohistRoot = path.join(skillDataRoot, 'skill-data', 'mohist');
const mohistStub = path.join(stubsRoot, 'mohist', 'SKILL.md');
const mohistFull = path.join(skillDataMohistRoot, 'SKILL.md');
const mohistRefs = path.join(skillDataMohistRoot, 'references', 'issue-templates.md');

describe('SkillDataService', () => {
  let service: SkillDataService;
  let tmpDir: string;

  beforeEach(() => {
    service = new SkillDataService();
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-skill-dynamic-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('discoverSkills', () => {
    it('returns visible built-in skills sorted by name', () => {
      const skills = service.discoverSkills();
      const visible = skills.filter(s => !s.hidden);
      expect(visible.length).toBeGreaterThan(0);
      const names = visible.map(s => s.name);
      expect(names).toEqual([...names].sort());
    });

    it('does not include hidden stubs as duplicate entries', () => {
      const skills = service.discoverSkills();
      const hidden = skills.filter(s => s.hidden);
      expect(hidden.length).toBe(0);
    });

    it('discovered skill has correct structure', () => {
      const skills = service.discoverSkills();
      const mohist = skills.find(s => s.name === 'mohist');
      expect(mohist).toBeDefined();
      expect(typeof mohist!.name).toBe('string');
      expect(typeof mohist!.description).toBe('string');
      expect(typeof mohist!.hidden).toBe('boolean');
      expect(typeof mohist!.path).toBe('string');
      expect(mohist!.hidden).toBe(false);
    });
  });

  describe('getSkillContent', () => {
    it('mo skills get mohist returns full packaged content', () => {
      const content = service.getSkillContent('mohist', false);
      expect(content.content).toBeTruthy();
      expect(content.content).not.toContain('获取完整指令');
      const stubContent = fs.readFileSync(mohistStub, 'utf-8');
      expect(content.content).not.toEqual(stubContent);
    });

    it('mo skills get mohist --full appends supplementary files', () => {
      const content = service.getSkillContent('mohist', true);
      expect(content.supplementaryFiles.length).toBeGreaterThan(0);
      const hasRefs = content.supplementaryFiles.some(f => f.path.includes('issue-templates'));
      expect(hasRefs).toBe(true);
    });

    it('supplementary files are sorted deterministically', () => {
      const content = service.getSkillContent('mohist', true);
      const paths = content.supplementaryFiles.map(f => f.path);
      expect(paths).toEqual([...paths].sort());
    });

    it('throws when skill not found', () => {
      expect(() => service.getSkillContent('nonexistent-skill-xyz', false)).toThrow(/not found/i);
    });
  });

  describe('resolveSkillPath', () => {
    it('mo skills path mohist prints packaged directory path', () => {
      const skillPath = service.resolveSkillPath('mohist');
      expect(skillPath).toBeTruthy();
      expect(fs.existsSync(skillPath!)).toBe(true);
    });

    it('returns null for unknown skill', () => {
      const skillPath = service.resolveSkillPath('nonexistent-skill-xyz');
      expect(skillPath).toBeNull();
    });
  });

  describe('MOHIST_SKILLS_DIR override', () => {
    it('overrides default skill asset discovery when set', () => {
      const tmpAssetRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-override-test-'));
      try {
        const testStubsDir = path.join(tmpAssetRoot, 'stubs');
        const testSkillDataDir = path.join(tmpAssetRoot, 'skill-data');

        fs.mkdirSync(path.join(testStubsDir, 'custom-skill'), { recursive: true });
        fs.writeFileSync(
          path.join(testStubsDir, 'custom-skill', 'SKILL.md'),
          '---\nname: custom-skill\ndescription: Custom test skill\nhidden: true\n---\nstub\n'
        );

        fs.mkdirSync(path.join(testSkillDataDir, 'custom-skill'), { recursive: true });
        fs.writeFileSync(
          path.join(testSkillDataDir, 'custom-skill', 'SKILL.md'),
          '---\nname: custom-skill\ndescription: Custom test skill\n---\nfull content here\n'
        );

        const originalEnv = process.env.MOHIST_SKILLS_DIR;
        process.env.MOHIST_SKILLS_DIR = tmpAssetRoot;

        const overrideService = new SkillDataService();
        const skills = overrideService.discoverSkills();
        const customSkill = skills.find(s => s.name === 'custom-skill');

        expect(customSkill).toBeDefined();
        expect(customSkill!.description).toBe('Custom test skill');

        if (originalEnv !== undefined) {
          process.env.MOHIST_SKILLS_DIR = originalEnv;
        } else {
          delete process.env.MOHIST_SKILLS_DIR;
        }
      } finally {
        fs.rmSync(tmpAssetRoot, { recursive: true, force: true });
      }
    });
  });
});

describe('Shared Agent Skills install', () => {
  let tmpDir: string;
  let service: SkillDataService;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-install-test-'));
    service = new SkillDataService();
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('stub-only install behavior', () => {
    it('installs lightweight stubs under 50 lines', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const content = fs.readFileSync(mohistPath, 'utf-8');
      const lines = content.split('\n').length;
      expect(lines).toBeLessThan(50);
    });

    it('installed stub contains hidden: true', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const content = fs.readFileSync(mohistPath, 'utf-8');
      expect(content).toContain('hidden: true');
    });

    it('mo skills install does NOT copy supplementary files into repository', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const mohistDir = path.join(tmpDir, '.agents', 'skills', 'mohist');
      const issueTemplatesPath = path.join(mohistDir, 'issue-templates.md');
      expect(fs.existsSync(issueTemplatesPath)).toBe(false);
      const refsDir = path.join(mohistDir, 'references');
      expect(fs.existsSync(refsDir)).toBe(false);
    });

    it('installs stubs for both mohist and mohist-explore', () => {
      const results = installSharedAgentSkills({ projectPath: tmpDir });
      expect(results.some(r => r.skill === 'mohist')).toBe(true);
      expect(results.some(r => r.skill === 'mohist-explore')).toBe(true);

      const mohistPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const explorePath = path.join(tmpDir, '.agents', 'skills', 'mohist-explore', 'SKILL.md');
      expect(fs.existsSync(mohistPath)).toBe(true);
      expect(fs.existsSync(explorePath)).toBe(true);
    });
  });

  describe('compatibility with preexisting full installed skills', () => {
    it('builtin get still serves packaged content even when stub is installed', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const stubPath = path.join(tmpDir, '.agents', 'skills', 'mohist', 'SKILL.md');
      const stubOriginal = fs.readFileSync(stubPath, 'utf-8');
      fs.writeFileSync(stubPath, '# Modified full content\nstub', 'utf-8');
      const content = service.getSkillContent('mohist', false);
      expect(content.content).not.toContain('Modified full content');
      expect(content.content).toBe(fs.readFileSync(mohistFull, 'utf-8'));
    });

    it('reinstall converts full installed skill back to stub', () => {
      const mohistDir = path.join(tmpDir, '.agents', 'skills', 'mohist');
      fs.mkdirSync(mohistDir, { recursive: true });
      fs.writeFileSync(
        path.join(mohistDir, 'SKILL.md'),
        '---\nname: mohist\ndescription: Old full content\n---\n# Full content here\n'
      );
      expect(fs.existsSync(path.join(mohistDir, 'issue-templates.md'))).toBe(false);
      installSharedAgentSkills({ projectPath: tmpDir });
      const installedContent = fs.readFileSync(path.join(mohistDir, 'SKILL.md'), 'utf-8');
      expect(installedContent).not.toContain('Full content here');
      expect(installedContent).toContain('hidden: true');
    });
  });

  describe('user-authored skill directories remain untouched', () => {
    it('does not modify unrelated user skill directories', () => {
      const userSkillDir = path.join(tmpDir, '.agents', 'skills', 'mohist-po');
      fs.mkdirSync(userSkillDir, { recursive: true });
      fs.writeFileSync(
        path.join(userSkillDir, 'SKILL.md'),
        '---\nname: mohist-po\ndescription: User custom skill\n---\nCustom content\n'
      );
      installSharedAgentSkills({ projectPath: tmpDir });
      const userSkillContent = fs.readFileSync(path.join(userSkillDir, 'SKILL.md'), 'utf-8');
      expect(userSkillContent).toContain('Custom content');
      expect(userSkillContent).toContain('User custom skill');
    });

    it('install creates only mohist and mohist-explore, not other names', () => {
      installSharedAgentSkills({ projectPath: tmpDir });
      const skillsDir = path.join(tmpDir, '.agents', 'skills');
      const entries = fs.readdirSync(skillsDir);
      expect(entries).toContain('mohist');
      expect(entries).toContain('mohist-explore');
      expect(entries).not.toContain('mohist-walkthrough');
      expect(entries.length).toBe(2);
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
});

describe('CLI skills command integration', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-cli-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('mo skills list returns visible built-in skills', () => {
    const service = new SkillDataService();
    const skills = service.discoverSkills();
    const visible = skills.filter(s => !s.hidden);
    const names = visible.map(s => s.name);
    expect(names).toContain('mohist');
    expect(names).toContain('mohist-explore');
  });

  it('mo skills list --json returns structured JSON', () => {
    const service = new SkillDataService();
    const skills = service.discoverSkills();
    const visible = skills.filter(s => !s.hidden);
    const jsonOutput = visible.map(s => ({
      name: s.name,
      description: s.description,
      hidden: s.hidden,
      path: s.path,
      stub: s.stub,
    }));
    const str = JSON.stringify(jsonOutput, null, 2);
    const parsed = JSON.parse(str);
    expect(parsed.length).toBeGreaterThan(0);
    expect(parsed[0]).toHaveProperty('name');
    expect(parsed[0]).toHaveProperty('description');
    expect(parsed[0]).toHaveProperty('hidden');
    expect(parsed[0]).toHaveProperty('path');
  });

  it('mo skills get --all returns all visible built-in skills with content', () => {
    const service = new SkillDataService();
    const skills = service.discoverSkills();
    const visible = skills.filter(s => !s.hidden);
    for (const skill of visible) {
      const content = service.getSkillContent(skill.name, false);
      expect(content.content).toBeTruthy();
      expect(content.name).toBe(skill.name);
    }
  });

  it('mo skills path returns a valid directory path', () => {
    const service = new SkillDataService();
    const skillPath = service.resolveSkillPath('mohist');
    expect(skillPath).toBeTruthy();
    expect(fs.existsSync(skillPath!)).toBe(true);
    expect(fs.readdirSync(skillPath!).length).toBeGreaterThan(0);
  });
});