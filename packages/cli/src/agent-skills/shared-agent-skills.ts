import * as fs from 'node:fs';
import * as path from 'node:path';

export type SkillResult = 'created' | 'updated';

export interface SkillOperationResult {
  skill: string;
  result: SkillResult;
}

const STUBS_DIR = path.join(__dirname, 'stubs');

interface SkillBundle {
  name: string;
  stubFile: string;
}

const SHARED_SKILL_BUNDLES: SkillBundle[] = [
  {
    name: 'mohist',
    stubFile: path.join(STUBS_DIR, 'mohist', 'SKILL.md'),
  },
  {
    name: 'mohist-explore',
    stubFile: path.join(STUBS_DIR, 'mohist-explore', 'SKILL.md'),
  },
];

function ensureDir(dirPath: string): void {
  if (!fs.existsSync(dirPath)) {
    fs.mkdirSync(dirPath, { recursive: true });
  }
}

export interface InstallOptions {
  projectPath?: string;
  claude?: boolean;
}

function skillsDirName(claude: boolean): string {
  return claude ? '.claude' : '.agents';
}

export function installSharedAgentSkills(options: InstallOptions = {}): SkillOperationResult[] {
  const basePath = options.projectPath || process.cwd();
  const skillsDir = path.join(basePath, skillsDirName(options.claude ?? false), 'skills');
  const results: SkillOperationResult[] = [];

  for (const bundle of SHARED_SKILL_BUNDLES) {
    const skillDir = path.join(skillsDir, bundle.name);
    const skillFilePath = path.join(skillDir, 'SKILL.md');

    const existed = fs.existsSync(skillFilePath);
    const content = fs.readFileSync(bundle.stubFile, 'utf-8');

    ensureDir(skillDir);
    fs.writeFileSync(skillFilePath, content, 'utf-8');

    results.push({
      skill: bundle.name,
      result: existed ? 'updated' : 'created',
    });
  }

  return results;
}

export function getSharedSkillNames(): string[] {
  return SHARED_SKILL_BUNDLES.map(b => b.name);
}