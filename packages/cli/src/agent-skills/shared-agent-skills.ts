import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { SkillDataService } from './skill-data-service';

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

const BUILT_IN_HERMES_SKILLS = ['mohist', 'mohist-explore'];

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

function getHermesHome(): string {
  return process.env.HERMES_HOME || path.join(os.homedir(), '.hermes');
}

function copyDirRecursive(src: string, dest: string): void {
  ensureDir(dest);
  const entries = fs.readdirSync(src, { withFileTypes: true });
  for (const entry of entries) {
    const srcPath = path.join(src, entry.name);
    const destPath = path.join(dest, entry.name);
    if (entry.isDirectory()) {
      copyDirRecursive(srcPath, destPath);
    } else {
      fs.copyFileSync(srcPath, destPath);
    }
  }
}

export interface HermesInstallOptions {
  hermesHome?: string;
}

export function installHermesSkills(options: HermesInstallOptions = {}): SkillOperationResult[] {
  const hermesHome = options.hermesHome || getHermesHome();
  const skillsRoot = path.join(hermesHome, 'skills');
  const results: SkillOperationResult[] = [];

  const skillService = new SkillDataService();

  for (const skillName of BUILT_IN_HERMES_SKILLS) {
    const srcDir = skillService.resolvePackagedSkillPath(skillName);
    if (!srcDir) {
      throw new Error(`Packaged Hermes skill not found: ${skillName}`);
    }

    const destDir = path.join(skillsRoot, skillName);
    const skillFilePath = path.join(destDir, 'SKILL.md');
    const existed = fs.existsSync(skillFilePath);

    if (fs.existsSync(destDir)) {
      fs.rmSync(destDir, { recursive: true, force: true });
    }
    copyDirRecursive(srcDir, destDir);

    results.push({
      skill: skillName,
      result: existed ? 'updated' : 'created',
    });
  }

  return results;
}

export function getSharedSkillNames(): string[] {
  return SHARED_SKILL_BUNDLES.map(b => b.name);
}
