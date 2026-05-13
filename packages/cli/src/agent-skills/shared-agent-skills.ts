import * as fs from 'node:fs';
import * as path from 'node:path';

export type SkillResult = 'created' | 'updated';

export interface SkillOperationResult {
  skill: string;
  result: SkillResult;
}

const TEMPLATES_DIR = path.join(__dirname, 'templates');
const AGENT_SKILLS_DIR = __dirname;

interface SkillBundle {
  name: string;
  skillFile: string;
  extraFiles: string[];
}

const SHARED_SKILL_BUNDLES: SkillBundle[] = [
  {
    name: 'mohist',
    skillFile: path.join(TEMPLATES_DIR, 'mohist.md'),
    extraFiles: ['issue-templates.md'],
  },
  {
    name: 'mohist-explore',
    skillFile: path.join(TEMPLATES_DIR, 'mohist-explore.md'),
    extraFiles: [],
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
    const content = fs.readFileSync(bundle.skillFile, 'utf-8');

    ensureDir(skillDir);
    fs.writeFileSync(skillFilePath, content, 'utf-8');

    results.push({
      skill: bundle.name,
      result: existed ? 'updated' : 'created',
    });

    for (const extraFile of bundle.extraFiles) {
      const sourcePath = path.join(AGENT_SKILLS_DIR, extraFile);
      const destPath = path.join(skillDir, extraFile);
      if (fs.existsSync(sourcePath)) {
        const extraContent = fs.readFileSync(sourcePath, 'utf-8');
        fs.writeFileSync(destPath, extraContent, 'utf-8');
      }
    }
  }

  return results;
}

export function getSharedSkillNames(): string[] {
  return SHARED_SKILL_BUNDLES.map(b => b.name);
}