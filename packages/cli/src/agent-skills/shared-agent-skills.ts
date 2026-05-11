import * as fs from 'node:fs';
import * as path from 'node:path';

export type SkillResult = 'created' | 'updated';

export interface SkillOperationResult {
  skill: string;
  result: SkillResult;
}

const TEMPLATES_DIR = path.join(__dirname, 'templates');

function loadTemplate(name: string): string {
  const templatePath = path.join(TEMPLATES_DIR, `${name}.md`);
  return fs.readFileSync(templatePath, 'utf-8');
}

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

  for (const name of getSharedSkillNames()) {
    const skillDir = path.join(skillsDir, name);
    const skillFilePath = path.join(skillDir, 'SKILL.md');

    const existed = fs.existsSync(skillFilePath);
    const content = loadTemplate(name);

    ensureDir(path.dirname(skillFilePath));
    fs.writeFileSync(skillFilePath, content, 'utf-8');

    results.push({
      skill: name,
      result: existed ? 'updated' : 'created',
    });
  }

  return results;
}

export function getSharedSkillNames(): string[] {
  const entries = fs.readdirSync(TEMPLATES_DIR, { withFileTypes: true });
  return entries
    .filter(e => e.isFile() && e.name.endsWith('.md'))
    .map(e => e.name.replace(/\.md$/, ''));
}