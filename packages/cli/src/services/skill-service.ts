import * as fs from 'fs';
import * as path from 'path';
import { runAcpSession } from '../agent-runtime/acp-session';
import { SkillRepo, type Skill, type CreateSkillData } from '../db/skill-repo';
import { SkillRunRepo, type SkillRun } from '../db/skill-run-repo';
import { IssueService } from './issue-service';
import type { EventBus } from './event-bus';
import { Log } from '../util/log';

const log = Log.create({ service: 'skill-service' });

interface ParsedFrontmatter {
  name: string;
  description: string;
  prompt: string;
}

function parseFrontmatter(content: string, dirName: string): ParsedFrontmatter {
  const trimmed = content.trimStart();

  if (!trimmed.startsWith('---')) {
    return {
      name: dirName,
      description: `Skill: ${dirName}`,
      prompt: content,
    };
  }

  const closingIndex = trimmed.indexOf('---', 3);
  if (closingIndex === -1) {
    return {
      name: dirName,
      description: `Skill: ${dirName}`,
      prompt: content,
    };
  }

  const yamlBlock = trimmed.slice(3, closingIndex).trim();
  const body = trimmed.slice(closingIndex + 3).trim();

  const metadata: Record<string, string> = {};
  const regex = /^(\w+):\s*(.+)$/gm;
  let match: RegExpExecArray | null;
  while ((match = regex.exec(yamlBlock)) !== null) {
    metadata[match[1]] = match[2].trim();
  }

  const prompt = metadata.prompt || body || '';
  const name = metadata.name || dirName;
  const description = metadata.description || dirName;

  return { name, description, prompt };
}

export interface SkillServiceDeps {
  skillRepo: SkillRepo;
  skillRunRepo: SkillRunRepo;
  issueService: IssueService;
  eventBus: EventBus;
  opencodeBinPath?: string;
}

export class SkillService {
  private skillRepo: SkillRepo;
  private skillRunRepo: SkillRunRepo;
  private issueService: IssueService;
  private eventBus: EventBus;
  private opencodeBinPath?: string;

  constructor(deps: SkillServiceDeps) {
    this.skillRepo = deps.skillRepo;
    this.skillRunRepo = deps.skillRunRepo;
    this.issueService = deps.issueService;
    this.eventBus = deps.eventBus;
    this.opencodeBinPath = deps.opencodeBinPath;
  }

  scanAndRegister(projectPath: string, projectId: string): Skill[] {
    const skillsDir = path.join(projectPath, '.mohist', 'skills');

    if (!fs.existsSync(skillsDir)) {
      log.info('Skills directory not found', { skillsDir });
      return [];
    }

    let entries: fs.Dirent[];
    try {
      entries = fs.readdirSync(skillsDir, { withFileTypes: true });
    } catch (err) {
      log.warn('Failed to read skills directory', { skillsDir, error: String(err) });
      return [];
    }

    const skills: Skill[] = [];

    for (const entry of entries) {
      if (!entry.isDirectory()) continue;

      const skillMdPath = path.join(skillsDir, entry.name, 'SKILL.md');
      if (!fs.existsSync(skillMdPath)) continue;

      try {
        const content = fs.readFileSync(skillMdPath, 'utf-8');
        const parsed = parseFrontmatter(content, entry.name);

        const existing = this.skillRepo.findByName(parsed.name);
        if (existing) {
          const updated = this.skillRepo.update(existing.id, {
            description: parsed.description,
            prompt: parsed.prompt,
          });
          if (updated) {
            skills.push(updated);
          }
          continue;
        }

        const data: CreateSkillData = {
          name: parsed.name,
          projectId,
          description: parsed.description,
          prompt: parsed.prompt,
          dirPath: path.join(skillsDir, entry.name),
        };
        skills.push(this.skillRepo.create(data));
      } catch (err) {
        log.warn('Failed to parse SKILL.md', { path: skillMdPath, error: String(err) });
      }
    }

    log.info('Skills registered', { count: skills.length });
    return skills;
  }

  run(skillName: string, projectId: string, projectPath: string): SkillRun {
    const skill = this.skillRepo.findByName(skillName);
    if (!skill) {
      throw new Error(`Skill not found: ${skillName}`);
    }

    const runRecord = this.skillRunRepo.create({
      skillId: skill.id,
      projectId,
    });

    this.eventBus.emit('skill_started', {
      skillName: skill.name,
      runId: runRecord.id,
      projectId,
    });

    this.executeAsync(skill, runRecord, projectId, projectPath);

    return runRecord;
  }

  getRuns(skillId: string): SkillRun[] {
    return this.skillRunRepo.findBySkillId(skillId);
  }

  getByName(name: string): Skill | null {
    return this.skillRepo.findByName(name);
  }

  getByProject(projectId: string): Skill[] {
    return this.skillRepo.findByProject(projectId);
  }

  private executeAsync(
    skill: Skill,
    runRecord: SkillRun,
    projectId: string,
    projectPath: string,
  ): void {
    runAcpSession({
      cwd: projectPath,
      task: skill.prompt,
      eventBus: this.eventBus,
      projectId,
      opencodeBinPath: this.opencodeBinPath,
      title: `Skill: ${skill.name}`,
    })
      .then((result) => {
        if (result.success) {
          this.handleSuccess(skill, runRecord, projectId, result.text);
        } else {
          this.handleFailure(skill, runRecord, projectId, result.error ?? 'Unknown error');
        }
      })
      .catch((err: unknown) => {
        this.handleFailure(skill, runRecord, projectId, err instanceof Error ? err.message : String(err));
      });
  }

  private handleSuccess(
    skill: Skill,
    runRecord: SkillRun,
    projectId: string,
    text: string,
  ): void {
    this.skillRunRepo.update(runRecord.id, {
      status: 'completed',
      output: text || null,
    });

    let issueId: string | undefined;

    try {
      const issue = this.createIssueFromOutput(skill, projectId, text);
      issueId = issue.id;

      this.skillRunRepo.update(runRecord.id, {
        issueId: issue.id,
      });
    } catch (err) {
      log.error('Failed to create issue from skill output', {
        skillName: skill.name,
        runId: runRecord.id,
        error: String(err),
      });
    }

    this.eventBus.emit('skill_completed', {
      skillName: skill.name,
      runId: runRecord.id,
      projectId,
      issueId,
    });
  }

  private handleFailure(
    skill: Skill,
    runRecord: SkillRun,
    projectId: string,
    error: string,
  ): void {
    this.skillRunRepo.update(runRecord.id, {
      status: 'failed',
      error,
    });

    this.eventBus.emit('skill_failed', {
      skillName: skill.name,
      runId: runRecord.id,
      projectId,
      error,
    });
  }

  private createIssueFromOutput(skill: Skill, projectId: string, text: string) {
    let title: string;
    let body: string;

    if (text.trim()) {
      const firstLine = text.split('\n')[0].trim();
      title = firstLine.replace(/^#+\s*/, '').trim() || `Skill result: ${skill.name}`;
      body = text;
    } else {
      title = `Skill result: ${skill.name}`;
      body = `Executed skill ${skill.name} with no output.`;
    }

    return this.issueService.create({
      projectId,
      title,
      body,
      labels: ['skill-generated'],
    });
  }
}
