import * as fs from 'node:fs';
import * as path from 'node:path';

export interface SkillInfo {
  name: string;
  description: string;
  hidden: boolean;
  dir: string;
  stub: boolean;
}

export interface SkillListEntry {
  name: string;
  description: string;
  hidden: boolean;
  path: string;
  stub: boolean;
}

export interface SkillContent {
  name: string;
  content: string;
  path: string;
  supplementaryFiles: Array<{ path: string; content: string }>;
}

const BUILT_IN_SKILL_NAMES = ['mohist', 'mohist-explore'];

function parseFrontmatter(content: string): { name?: string; description?: string; hidden?: boolean } | null {
  const match = content.match(/^---\n([\s\S]*?)\n---/);
  if (!match) return null;
  const fm: Record<string, string> = {};
  for (const line of match[1].split('\n')) {
    const colonIdx = line.indexOf(':');
    if (colonIdx === -1) continue;
    const key = line.slice(0, colonIdx).trim();
    const value = line.slice(colonIdx + 1).trim();
    fm[key] = value;
  }
  return {
    name: fm.name,
    description: fm.description,
    hidden: fm.hidden === 'true',
  };
}

function readSkillMeta(filePath: string): { name?: string; description?: string; hidden?: boolean; dir: string; stub: boolean } | null {
  if (!fs.existsSync(filePath)) return null;
  const content = fs.readFileSync(filePath, 'utf-8');
  const parsed = parseFrontmatter(content);
  if (!parsed) return null;
  return {
    name: parsed.name,
    description: parsed.description,
    hidden: parsed.hidden ?? false,
    dir: path.dirname(filePath),
    stub: filePath.includes('/stubs/'),
  };
}

export class SkillDataService {
  private skillDataRoot: string;

  constructor() {
    this.skillDataRoot = this.resolveSkillDataRoot();
  }

  private resolveSkillDataRoot(): string {
    if (process.env.MOHIST_SKILLS_DIR) {
      const override = process.env.MOHIST_SKILLS_DIR;
      if (fs.existsSync(override)) return override;
    }
    const possibleRoots = this.findPossibleRoots();
    for (const root of possibleRoots) {
      if (fs.existsSync(root)) return root;
    }
    return possibleRoots[0];
  }

  private findPossibleRoots(): string[] {
    const binPath = __dirname;
    const distAgentSkills = path.join(binPath, 'agent-skills');
    const srcAgentSkills = path.resolve(binPath, '../../src/agent-skills');
    return [distAgentSkills, srcAgentSkills];
  }

  private collectSupplementary(dir: string, subdirs: string[]): Array<{ path: string; content: string }> {
    const files: Array<{ path: string; content: string }> = [];
    for (const subdir of subdirs) {
      const subPath = path.join(dir, subdir);
      if (!fs.existsSync(subPath)) continue;
      const entries = fs.readdirSync(subPath, { withFileTypes: true });
      const sortedEntries = entries.filter(e => e.isFile()).sort((a, b) => a.name.localeCompare(b.name));
      for (const entry of sortedEntries) {
        const filePath = path.join(subPath, entry.name);
        const relPath = `${subdir}/${entry.name}`;
        files.push({ path: relPath, content: fs.readFileSync(filePath, 'utf-8') });
      }
    }
    return files;
  }

  discoverSkills(): SkillListEntry[] {
    const root = this.skillDataRoot;
    const stubsDir = path.join(root, 'stubs');
    const skillDataDir = path.join(root, 'skill-data');
    const found = new Map<string, SkillListEntry>();
    for (const dir of [stubsDir, skillDataDir]) {
      if (!fs.existsSync(dir)) continue;
      const entries = fs.readdirSync(dir, { withFileTypes: true });
      for (const entry of entries) {
        if (!entry.isDirectory()) continue;
        const skillName = entry.name;
        const skillFile = path.join(dir, entry.name, 'SKILL.md');
        if (!fs.existsSync(skillFile)) continue;
        const meta = readSkillMeta(skillFile);
        if (!meta || !meta.name) continue;
        const isFromSkillData = dir === skillDataDir;
        const key = skillName;
        const existing = found.get(key);
        if (existing && !isFromSkillData) continue;
        if (isFromSkillData || !existing) {
          found.set(key, {
            name: meta.name,
            description: meta.description ?? '',
            hidden: meta.hidden ?? false,
            path: path.dirname(skillFile),
            stub: !isFromSkillData,
          });
        }
      }
    }
    return Array.from(found.values()).sort((a, b) => a.name.localeCompare(b.name));
  }

  getSkillContent(name: string, full: boolean = false): SkillContent {
    const root = this.skillDataRoot;
    const skillDataDir = path.join(root, 'skill-data', name);
    const stubDir = path.join(root, 'stubs', name);
    const preferredDir = fs.existsSync(skillDataDir) ? skillDataDir : fs.existsSync(stubDir) ? stubDir : null;
    if (!preferredDir) throw new Error(`Skill not found: ${name}`);
    const skillFile = path.join(preferredDir, 'SKILL.md');
    if (!fs.existsSync(skillFile)) throw new Error(`Skill not found: ${name}`);
    const content = fs.readFileSync(skillFile, 'utf-8');
    const supplementary: Array<{ path: string; content: string }> = full ? this.collectSupplementary(preferredDir, ['references', 'templates']) : [];
    return { name, content, path: preferredDir, supplementaryFiles: supplementary };
  }

  getBuiltInNames(): string[] {
    return [...BUILT_IN_SKILL_NAMES];
  }

  resolveSkillPath(name: string): string | null {
    const root = this.skillDataRoot;
    const skillDataDir = path.join(root, 'skill-data', name);
    const stubDir = path.join(root, 'stubs', name);
    if (fs.existsSync(skillDataDir)) return skillDataDir;
    if (fs.existsSync(stubDir)) return stubDir;
    return null;
  }

  getSkillDataRoot(): string {
    return this.skillDataRoot;
  }
}