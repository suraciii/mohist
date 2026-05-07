import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

vi.mock('../src/agent-runtime/agent-session', () => ({
  withSession: vi.fn(),
}));

import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { SkillRepo } from '../src/db/skill-repo';
import { SkillRunRepo } from '../src/db/skill-run-repo';
import { IssueService } from '../src/services/issue-service';
import { EventBus } from '../src/services/event-bus';
import { SkillService } from '../src/services/skill-service';
import { withSession } from '../src/agent-runtime/agent-session';

const mockWithSession = vi.mocked(withSession);

describe('SkillService', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let commentRepo: CommentRepo;
  let skillRepo: SkillRepo;
  let skillRunRepo: SkillRunRepo;
  let issueService: IssueService;
  let eventBus: EventBus;
  let service: SkillService;
  let tmpDir: string;
  let projectId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    commentRepo = new CommentRepo(db);
    skillRepo = new SkillRepo(db);
    skillRunRepo = new SkillRunRepo(db);
    issueService = new IssueService(issueRepo, commentRepo);
    eventBus = new EventBus();
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-skill-test-'));

    const project = projectRepo.create({ name: 'Test', path: tmpDir });
    projectId = project.id;

    service = new SkillService({
      skillRepo,
      skillRunRepo,
      issueService,
      eventBus,
    });
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('parseFrontmatter (via scanAndRegister)', () => {
    it('should parse full frontmatter with name, description, and prompt', () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'my-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: analyze-code',
        'description: Analyze the codebase',
        'prompt: Please analyze this codebase and create issues',
        '---',
        '',
        '## Additional Context',
        'This is the body.',
      ].join('\n'));

      const skills = service.scanAndRegister(tmpDir, projectId);

      expect(skills).toHaveLength(1);
      expect(skills[0].name).toBe('analyze-code');
      expect(skills[0].description).toBe('Analyze the codebase');
      expect(skills[0].prompt).toBe('Please analyze this codebase and create issues');
    });

    it('should use body as prompt when frontmatter has no prompt field', () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'body-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: body-only',
        'description: Uses body as prompt',
        '---',
        'This is the body content that becomes the prompt.',
      ].join('\n'));

      const skills = service.scanAndRegister(tmpDir, projectId);

      expect(skills).toHaveLength(1);
      expect(skills[0].name).toBe('body-only');
      expect(skills[0].prompt).toBe('This is the body content that becomes the prompt.');
    });

    it('should fall back to dirName when no frontmatter present', () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'no-fm-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), 'Just plain markdown content without frontmatter.');

      const skills = service.scanAndRegister(tmpDir, projectId);

      expect(skills).toHaveLength(1);
      expect(skills[0].name).toBe('no-fm-skill');
      expect(skills[0].description).toBe('Skill: no-fm-skill');
      expect(skills[0].prompt).toBe('Just plain markdown content without frontmatter.');
    });

    it('should handle missing description by falling back to dirName', () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'no-desc');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: partial-skill',
        'prompt: Do something',
        '---',
        'Body here.',
      ].join('\n'));

      const skills = service.scanAndRegister(tmpDir, projectId);

      expect(skills).toHaveLength(1);
      expect(skills[0].name).toBe('partial-skill');
      expect(skills[0].description).toBe('no-desc');
      expect(skills[0].prompt).toBe('Do something');
    });
  });

  describe('scanAndRegister', () => {
    it('should discover skills from .mohist/skills/ directory', () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'analyze');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: analyze',
        'description: Analyze code',
        'prompt: Analyze this',
        '---',
      ].join('\n'));

      const skills = service.scanAndRegister(tmpDir, projectId);

      expect(skills).toHaveLength(1);
      expect(skills[0].name).toBe('analyze');
    });

    it('should return empty array when .mohist/skills/ directory does not exist', () => {
      const skills = service.scanAndRegister(tmpDir, projectId);
      expect(skills).toEqual([]);
    });

    it('should skip subdirectories without SKILL.md', () => {
      const skillsBase = path.join(tmpDir, '.mohist', 'skills');
      fs.mkdirSync(path.join(skillsBase, 'with-skill'), { recursive: true });
      fs.mkdirSync(path.join(skillsBase, 'no-skill'), { recursive: true });
      fs.writeFileSync(
        path.join(skillsBase, 'with-skill', 'SKILL.md'),
        '---\nname: with-skill\ndescription: desc\nprompt: p\n---',
      );

      const skills = service.scanAndRegister(tmpDir, projectId);

      expect(skills).toHaveLength(1);
      expect(skills[0].name).toBe('with-skill');
    });

    it('should skip files (non-directories) in skills directory', () => {
      const skillsBase = path.join(tmpDir, '.mohist', 'skills');
      fs.mkdirSync(skillsBase, { recursive: true });
      fs.writeFileSync(path.join(skillsBase, 'README.md'), 'not a skill');

      const skills = service.scanAndRegister(tmpDir, projectId);
      expect(skills).toEqual([]);
    });

    it('should update existing skill on re-scan', () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'updatable');
      fs.mkdirSync(skillDir, { recursive: true });

      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: updatable',
        'description: Original desc',
        'prompt: Original prompt',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: updatable',
        'description: Updated desc',
        'prompt: Updated prompt',
        '---',
      ].join('\n'));

      const skills = service.scanAndRegister(tmpDir, projectId);

      expect(skills).toHaveLength(1);
      expect(skills[0].description).toBe('Updated desc');
      expect(skills[0].prompt).toBe('Updated prompt');

      const all = service.getByProject(projectId);
      expect(all).toHaveLength(1);
    });
  });

  describe('run() success path', () => {
    it('should create completed run record and create Issue', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'test-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: test-skill',
        'description: Test',
        'prompt: Do the thing',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      mockWithSession.mockResolvedValue({
        text: '# Refactor Authentication Module\n\nHere is my analysis...',
        success: true,
      });

      const runRecord = service.run('test-skill', projectId, tmpDir);

      expect(runRecord.status).toBe('running');

      await vi.waitFor(() => {
        const updated = skillRunRepo.findById(runRecord.id);
        expect(updated?.status).toBe('completed');
      });

      const completed = skillRunRepo.findById(runRecord.id)!;
      expect(completed.output).toBe('# Refactor Authentication Module\n\nHere is my analysis...');
      expect(completed.issueId).not.toBeNull();

      const issue = issueRepo.findById(completed.issueId!);
      expect(issue).not.toBeNull();
      expect(issue!.title).toBe('Refactor Authentication Module');
      expect(issue!.body).toBe('# Refactor Authentication Module\n\nHere is my analysis...');
      expect(issue!.labels).toContain('skill-generated');
    });

    it('should emit skill_started and skill_completed events', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'event-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: event-skill',
        'description: Events',
        'prompt: Do it',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      const startedEvents: unknown[] = [];
      const completedEvents: unknown[] = [];
      eventBus.on('skill_started', (d) => startedEvents.push(d));
      eventBus.on('skill_completed', (d) => completedEvents.push(d));

      mockWithSession.mockResolvedValue({
        text: 'Some output',
        success: true,
      });

      service.run('event-skill', projectId, tmpDir);

      await vi.waitFor(() => {
        expect(completedEvents).toHaveLength(1);
      });

      expect(startedEvents).toHaveLength(1);
      expect((startedEvents[0] as { skillName: string }).skillName).toBe('event-skill');
      expect((completedEvents[0] as { skillName: string }).skillName).toBe('event-skill');
    });
  });

  describe('run() failure path', () => {
    it('should record failure when ACP session returns success=false', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'fail-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: fail-skill',
        'description: Fail',
        'prompt: Fail this',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      mockWithSession.mockResolvedValue({
        text: '',
        success: false,
        error: 'Agent timeout',
      });

      const runRecord = service.run('fail-skill', projectId, tmpDir);

      await vi.waitFor(() => {
        const updated = skillRunRepo.findById(runRecord.id);
        expect(updated?.status).toBe('failed');
      });

      const failed = skillRunRepo.findById(runRecord.id)!;
      expect(failed.error).toBe('Agent timeout');
      expect(failed.issueId).toBeNull();
    });

    it('should record failure when ACP session throws', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'throw-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: throw-skill',
        'description: Throw',
        'prompt: Throw error',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      mockWithSession.mockRejectedValue(new Error('spawn failed'));

      const runRecord = service.run('throw-skill', projectId, tmpDir);

      await vi.waitFor(() => {
        const updated = skillRunRepo.findById(runRecord.id);
        expect(updated?.status).toBe('failed');
      });

      const failed = skillRunRepo.findById(runRecord.id)!;
      expect(failed.error).toBe('spawn failed');
    });

    it('should emit skill_failed event', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'fail-event');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: fail-event',
        'description: Fail event',
        'prompt: Fail',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      const failedEvents: unknown[] = [];
      eventBus.on('skill_failed', (d) => failedEvents.push(d));

      mockWithSession.mockResolvedValue({
        text: '',
        success: false,
        error: 'Something went wrong',
      });

      service.run('fail-event', projectId, tmpDir);

      await vi.waitFor(() => {
        expect(failedEvents).toHaveLength(1);
      });

      expect((failedEvents[0] as { error: string }).error).toBe('Something went wrong');
    });
  });

  describe('run() skill not found', () => {
    it('should throw when skill name does not exist', () => {
      expect(() => service.run('nonexistent', projectId, tmpDir)).toThrow(
        'Skill not found: nonexistent',
      );
    });
  });

  describe('Issue title extraction', () => {
    it('should extract title from first line with markdown heading', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'title-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: title-skill',
        'description: Title',
        'prompt: Run',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      mockWithSession.mockResolvedValue({
        text: '### Fix memory leak in cache\n\nDetails here.',
        success: true,
      });

      const runRecord = service.run('title-skill', projectId, tmpDir);

      await vi.waitFor(() => {
        const updated = skillRunRepo.findById(runRecord.id);
        expect(updated?.status).toBe('completed');
      });

      const completed = skillRunRepo.findById(runRecord.id)!;
      const issue = issueRepo.findById(completed.issueId!);
      expect(issue!.title).toBe('Fix memory leak in cache');
    });

    it('should use fallback title when output is empty', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'empty-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: empty-skill',
        'description: Empty',
        'prompt: Run',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      mockWithSession.mockResolvedValue({
        text: '',
        success: true,
      });

      const runRecord = service.run('empty-skill', projectId, tmpDir);

      await vi.waitFor(() => {
        const updated = skillRunRepo.findById(runRecord.id);
        expect(updated?.status).toBe('completed');
      });

      const completed = skillRunRepo.findById(runRecord.id)!;
      const issue = issueRepo.findById(completed.issueId!);
      expect(issue!.title).toBe('Skill result: empty-skill');
    });

    it('should use first line as title when no markdown heading', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'plain-title');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: plain-title',
        'description: Plain',
        'prompt: Run',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      mockWithSession.mockResolvedValue({
        text: 'This is a plain text first line\n\nMore details here.',
        success: true,
      });

      const runRecord = service.run('plain-title', projectId, tmpDir);

      await vi.waitFor(() => {
        const updated = skillRunRepo.findById(runRecord.id);
        expect(updated?.status).toBe('completed');
      });

      const completed = skillRunRepo.findById(runRecord.id)!;
      const issue = issueRepo.findById(completed.issueId!);
      expect(issue!.title).toBe('This is a plain text first line');
    });
  });

  describe('Issue creation failure does not block run completion', () => {
    it('should complete run record even if IssueService throws', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'issue-fail');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: issue-fail',
        'description: Issue fail',
        'prompt: Run',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      const failingIssueService = new IssueService(issueRepo, commentRepo);
      const createSpy = vi.spyOn(failingIssueService, 'create').mockImplementation(() => {
        throw new Error('DB write failed');
      });

      const svc = new SkillService({
        skillRepo,
        skillRunRepo,
        issueService: failingIssueService,
        eventBus,
      });

      mockWithSession.mockResolvedValue({
        text: 'Some output',
        success: true,
      });

      const runRecord = svc.run('issue-fail', projectId, tmpDir);

      await vi.waitFor(() => {
        const updated = skillRunRepo.findById(runRecord.id);
        expect(updated?.status).toBe('completed');
      });

      const completed = skillRunRepo.findById(runRecord.id)!;
      expect(completed.status).toBe('completed');
      expect(completed.output).toBe('Some output');
      expect(completed.issueId).toBeNull();
      expect(createSpy).toHaveBeenCalled();
    });
  });

  describe('getRuns', () => {
    it('should return run history for a skill', async () => {
      const skillDir = path.join(tmpDir, '.mohist', 'skills', 'history-skill');
      fs.mkdirSync(skillDir, { recursive: true });
      fs.writeFileSync(path.join(skillDir, 'SKILL.md'), [
        '---',
        'name: history-skill',
        'description: History',
        'prompt: Run',
        '---',
      ].join('\n'));

      service.scanAndRegister(tmpDir, projectId);

      mockWithSession.mockResolvedValue({ text: 'ok', success: true });

      const skill = service.getByName('history-skill')!;
      service.run('history-skill', projectId, tmpDir);

      await vi.waitFor(() => {
        expect(service.getRuns(skill.id)).toHaveLength(1);
      });

      const runs = service.getRuns(skill.id);
      expect(runs[0].skillId).toBe(skill.id);
    });
  });
});
