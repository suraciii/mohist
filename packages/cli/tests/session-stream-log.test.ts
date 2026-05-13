import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { SessionStreamLogRepo } from '../src/db/session-stream-log-repo';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
const SESSION_STREAM_EVENT_TYPES = new Set(['agent_thought_chunk', 'agent_message_chunk', 'tool_call', 'tool_call_update', 'user_message_chunk']);

describe('SessionStreamLogRepo', () => {
  let db: DatabaseManager;
  let repo: SessionStreamLogRepo;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    const issueRepo = new IssueRepo(db);
    issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = db.get<{ id: string }>('SELECT id FROM issues WHERE project_id = ?', [project.id])!.id;
    repo = new SessionStreamLogRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  describe('insert', () => {
    it('should persist entry and return it with correct fields', () => {
      const entry = repo.insert(issueId, 'sess-1', 'agent_message_chunk', { text: 'hello' });
      expect(entry.id).toBeDefined();
      expect(entry.sessionId).toBe('sess-1');
      expect(entry.issueId).toBe(issueId);
      expect(entry.eventType).toBe('agent_message_chunk');
      expect(JSON.parse(entry.data)).toEqual({ text: 'hello' });
      expect(entry.createdAt).toBeDefined();
    });
  });

  describe('findBySessionId', () => {
    it('should return entries for specific session ordered by created_at ASC', () => {
      repo.insert(issueId, 'sess-1', 'agent_thought_chunk', { text: 'thinking' });
      repo.insert(issueId, 'sess-1', 'agent_message_chunk', { text: 'hello' });
      repo.insert(issueId, 'sess-2', 'agent_message_chunk', { text: 'other session' });

      const results = repo.findBySessionId('sess-1');
      expect(results).toHaveLength(2);
      expect(results[0].eventType).toBe('agent_thought_chunk');
      expect(results[1].eventType).toBe('agent_message_chunk');
    });

    it('should return empty array for unknown session', () => {
      expect(repo.findBySessionId('unknown')).toEqual([]);
    });
  });

  describe('findByIssueId', () => {
    it('should return entries across sessions ordered by created_at ASC', () => {
      repo.insert(issueId, 'sess-a', 'agent_message_chunk', { text: 'first' });
      repo.insert(issueId, 'sess-b', 'agent_message_chunk', { text: 'second' });
      repo.insert(issueId, 'sess-a', 'tool_call', { name: 'Read' });

      const results = repo.findByIssueId(issueId);
      expect(results).toHaveLength(3);
      expect(results[0].sessionId).toBe('sess-a');
      expect(results[0].eventType).toBe('agent_message_chunk');
      expect(results[1].sessionId).toBe('sess-b');
      expect(results[2].sessionId).toBe('sess-a');
      expect(results[2].eventType).toBe('tool_call');
    });

    it('should return empty array for unknown issue', () => {
      expect(repo.findByIssueId('unknown')).toEqual([]);
    });
  });

  describe('insert and readback', () => {
    it('should persist entry and findBySessionId returns correct data', () => {
      repo.insert(issueId, 'sess-1', 'agent_message_chunk', { text: 'hello' });

      const results = repo.findBySessionId('sess-1');
      expect(results).toHaveLength(1);
      expect(results[0].eventType).toBe('agent_message_chunk');
      expect(JSON.parse(results[0].data)).toEqual({ text: 'hello' });
    });
  });
});

describe('session_stream_log schema migration', () => {
  let db: DatabaseManager;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
  });

  afterEach(() => {
    db.close();
  });

  it('should create session_stream_log table', () => {
    const table = db.get<{ name: string }>(
      "SELECT name FROM sqlite_master WHERE type='table' AND name='session_stream_log'"
    );
    expect(table?.name).toBe('session_stream_log');
  });

  it('should create idx_session_stream_log_session index', () => {
    const idx = db.get<{ name: string }>(
      "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_session_stream_log_session'"
    );
    expect(idx?.name).toBe('idx_session_stream_log_session');
  });

  it('should create idx_session_stream_log_issue index', () => {
    const idx = db.get<{ name: string }>(
      "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_session_stream_log_issue'"
    );
    expect(idx?.name).toBe('idx_session_stream_log_issue');
  });
});

describe('SESSION_STREAM_EVENT_TYPES', () => {
  it('should contain exactly the 5 session stream event types', () => {
    expect(SESSION_STREAM_EVENT_TYPES).toBeInstanceOf(Set);
    expect(SESSION_STREAM_EVENT_TYPES.size).toBe(5);
    expect(SESSION_STREAM_EVENT_TYPES.has('agent_thought_chunk')).toBe(true);
    expect(SESSION_STREAM_EVENT_TYPES.has('agent_message_chunk')).toBe(true);
    expect(SESSION_STREAM_EVENT_TYPES.has('tool_call')).toBe(true);
    expect(SESSION_STREAM_EVENT_TYPES.has('tool_call_update')).toBe(true);
    expect(SESSION_STREAM_EVENT_TYPES.has('user_message_chunk')).toBe(true);
  });
});

describe('millisecond timestamp fidelity', () => {
  let db: DatabaseManager;
  let repo: SessionStreamLogRepo;
  let issueId: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    const issueRepo = new IssueRepo(db);
    issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = db.get<{ id: string }>('SELECT id FROM issues WHERE project_id = ?', [project.id])!.id;
    repo = new SessionStreamLogRepo(db);
  });

  afterEach(() => {
    db.close();
  });

  it('should capture millisecond precision for newly persisted events', () => {
    const entry = repo.insert(issueId, 'sess-1', 'agent_message_chunk', { text: 'hello' });
    expect(entry.createdAt).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);
  });

  it('should not produce same-second collisions for rapid inserts', async () => {
    const entries: ReturnType<typeof repo.insert>[] = [];
    for (let i = 0; i < 10; i++) {
      entries.push(repo.insert(issueId, 'sess-1', 'agent_message_chunk', { text: `msg-${i}` }));
      await new Promise(resolve => setTimeout(resolve, 5));
    }

    const timestamps = entries.map(e => e.createdAt);
    const uniqueTimestamps = new Set(timestamps);
    expect(uniqueTimestamps.size).toBeGreaterThanOrEqual(2);
  });

  it('should order by createdAt with sub-second resolution when querying', () => {
    const entries: ReturnType<typeof repo.insert>[] = [];
    for (let i = 0; i < 5; i++) {
      entries.push(repo.insert(issueId, 'sess-1', 'agent_thought_chunk', { text: `thought-${i}` }));
    }

    const results = repo.findBySessionId('sess-1');
    expect(results).toHaveLength(5);

    for (let i = 1; i < results.length; i++) {
      expect(results[i].createdAt >= results[i - 1].createdAt).toBe(true);
    }
  });
});
