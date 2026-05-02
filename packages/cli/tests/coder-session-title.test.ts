import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { CoderSessionRepo } from '../src/db/coder-session-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';

describe('CoderSessionRepo title', () => {
  let db: DatabaseManager;
  let repo: CoderSessionRepo;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;
    repo = new CoderSessionRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  it('insert with title stores correctly', () => {
    const session = repo.insert({
      issueId,
      acpSessionId: 'acp-1',
      executionId: 'build-1-T-004',
      taskDescription: 'some task',
      title: 'T-004: Create Plan',
    });

    expect(session.title).toBe('T-004: Create Plan');

    const found = repo.findByIssueId(issueId);
    expect(found).toHaveLength(1);
    expect(found[0].title).toBe('T-004: Create Plan');
  });

  it('insert without title stores NULL', () => {
    const session = repo.insert({
      issueId,
      acpSessionId: 'acp-2',
      executionId: 'build-1-T-005',
      taskDescription: 'some task',
    });

    expect(session.title).toBeNull();

    const found = repo.findByIssueId(issueId);
    expect(found).toHaveLength(1);
    expect(found[0].title).toBeNull();
  });

  it('insert with empty title string stores NULL', () => {
    const session = repo.insert({
      issueId,
      acpSessionId: 'acp-3',
      executionId: 'build-1-T-006',
      taskDescription: 'some task',
      title: undefined,
    });

    expect(session.title).toBeNull();
  });

  it('migration adds title column to coder_session', () => {
    const tableInfo = db.all<{ name: string }>("PRAGMA table_info(coder_session)");
    const hasTitle = tableInfo.some(col => col.name === 'title');
    expect(hasTitle).toBe(true);
  });

  it('findByIssueId returns title field for each session', () => {
    repo.insert({
      issueId,
      acpSessionId: 'acp-4',
      executionId: 'build-1-T-001',
      title: 'T-001: First task',
    });
    repo.insert({
      issueId,
      acpSessionId: 'acp-5',
      executionId: 'build-1-T-002',
    });

    const sessions = repo.findByIssueId(issueId);
    expect(sessions).toHaveLength(2);
    const withTitle = sessions.find(s => s.acpSessionId === 'acp-4');
    const withoutTitle = sessions.find(s => s.acpSessionId === 'acp-5');
    expect(withTitle!.title).toBe('T-001: First task');
    expect(withoutTitle!.title).toBeNull();
  });
});

describe('getSessionLabel fallback chain', () => {
  let getSessionLabel: (session: {
    title: string | null;
    executionId: string | null;
    stage: string | null;
    taskDescription: string | null;
  }) => string;

  beforeEach(async () => {
    const mod = await import('../web/src/components/SessionHeader');
    getSessionLabel = mod.getSessionLabel;
  });

  it('returns title when present', () => {
    const result = getSessionLabel({
      title: 'T-004: Create Plan',
      executionId: 'build-127-T-004',
      stage: 'build',
      taskDescription: 'some long description',
    });
    expect(result).toBe('T-004: Create Plan');
  });

  it('parses T-xxx from executionId when no title', () => {
    const result = getSessionLabel({
      title: null,
      executionId: 'build-127-T-004',
      stage: 'build',
      taskDescription: 'some long description',
    });
    expect(result).toBe('T-004');
  });

  it('falls back to stage name from executionId prefix', () => {
    const result = getSessionLabel({
      title: null,
      executionId: 'plan-127-step',
      stage: null,
      taskDescription: 'some long description',
    });
    expect(result).toBe('Plan');
  });

  it('falls back to stage name from Check executionId prefix', () => {
    const result = getSessionLabel({
      title: null,
      executionId: 'check-127-step',
      stage: null,
      taskDescription: 'some long description',
    });
    expect(result).toBe('Check');
  });

  it('falls back to Build from executionId prefix', () => {
    const result = getSessionLabel({
      title: null,
      executionId: 'build-127',
      stage: null,
      taskDescription: 'some long description',
    });
    expect(result).toBe('Build');
  });

  it('falls back to stage field when executionId prefix is unrecognized', () => {
    const result = getSessionLabel({
      title: null,
      executionId: null,
      stage: 'plan',
      taskDescription: 'some long description',
    });
    expect(result).toBe('Plan');
  });

  it('falls back to stage=check', () => {
    const result = getSessionLabel({
      title: null,
      executionId: null,
      stage: 'check',
      taskDescription: 'some long description',
    });
    expect(result).toBe('Check');
  });

  it('falls back to taskDescription truncated to 24 chars', () => {
    const result = getSessionLabel({
      title: null,
      executionId: null,
      stage: null,
      taskDescription: 'This is a very long task description that exceeds 24 characters easily',
    });
    expect(result).toBe('This is a very long t...');
    expect(result.length).toBeLessThanOrEqual(24);
  });

  it('returns taskDescription as-is when under 24 chars', () => {
    const result = getSessionLabel({
      title: null,
      executionId: null,
      stage: null,
      taskDescription: 'Short task',
    });
    expect(result).toBe('Short task');
  });

  it('returns Session when all fields are null', () => {
    const result = getSessionLabel({
      title: null,
      executionId: null,
      stage: null,
      taskDescription: null,
    });
    expect(result).toBe('Session');
  });

  it('returns Session when title is empty string', () => {
    const result = getSessionLabel({
      title: '',
      executionId: null,
      stage: null,
      taskDescription: null,
    });
    expect(result).toBe('Session');
  });
});
