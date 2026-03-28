import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { DatabaseManager, resetDatabase, closeDatabase } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { CommentRepo } from '../src/db/comment-repo';
import { LabelRepo } from '../src/db/label-repo';
import { Stage, IssueStatus } from '../src/types';

describe('DatabaseManager', () => {
  let db: DatabaseManager;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('constructor', () => {
    it('should create in-memory database', () => {
      const memoryDb = new DatabaseManager({ inMemory: true });
      expect(memoryDb.getDbPath()).toBe(':memory:');
      memoryDb.close();
    });

    it('should use memory journal mode for in-memory database', () => {
      const result = db.get<{ journal_mode: string }>('PRAGMA journal_mode');
      expect(result?.journal_mode).toBe('memory');
    });

    it('should enable foreign keys', () => {
      const result = db.get<{ foreign_keys: number }>('PRAGMA foreign_keys');
      expect(result?.foreign_keys).toBe(1);
    });
  });

  describe('run/get/all', () => {
    it('should execute run statements', () => {
      const result = db.run('INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)', 
        ['test-id', 'Test Project', '/test/path', '2024-01-01', '2024-01-01']);
      expect(result.changes).toBe(1);
    });

    it('should get single row', () => {
      db.run('INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)', 
        ['test-id', 'Test Project', '/test/path', '2024-01-01', '2024-01-01']);
      
      const row = db.get<{ id: string; name: string }>('SELECT * FROM projects WHERE id = ?', ['test-id']);
      expect(row?.name).toBe('Test Project');
    });

    it('should return undefined for non-existent row', () => {
      const row = db.get<{ id: string }>('SELECT * FROM projects WHERE id = ?', ['nonexistent']);
      expect(row).toBeUndefined();
    });

    it('should get all rows', () => {
      db.run('INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)', 
        ['id-1', 'Project 1', '/path/1', '2024-01-01', '2024-01-01']);
      db.run('INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)', 
        ['id-2', 'Project 2', '/path/2', '2024-01-01', '2024-01-01']);
      
      const rows = db.all<{ id: string }>('SELECT * FROM projects ORDER BY name');
      expect(rows).toHaveLength(2);
      expect(rows[0].name).toBe('Project 1');
      expect(rows[1].name).toBe('Project 2');
    });
  });

  describe('transaction', () => {
    it('should commit transaction on success', () => {
      db.transaction(() => {
        db.run('INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)', 
          ['id-1', 'Project 1', '/path/1', '2024-01-01', '2024-01-01']);
        db.run('INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)', 
          ['id-2', 'Project 2', '/path/2', '2024-01-01', '2024-01-01']);
      });

      const count = db.get<{ count: number }>('SELECT COUNT(*) as count FROM projects');
      expect(count?.count).toBe(2);
    });

    it('should rollback transaction on error', () => {
      try {
        db.transaction(() => {
          db.run('INSERT INTO projects (id, name, path, created_at, updated_at) VALUES (?, ?, ?, ?, ?)', 
            ['id-1', 'Project 1', '/path/1', '2024-01-01', '2024-01-01']);
          throw new Error('Test error');
        });
      } catch (e) {
        // Expected
      }

      const count = db.get<{ count: number }>('SELECT COUNT(*) as count FROM projects');
      expect(count?.count).toBe(0);
    });
  });
});

describe('ProjectRepo', () => {
  let db: DatabaseManager;
  let repo: ProjectRepo;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    repo = new ProjectRepo(db);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('create', () => {
    it('should create a project', () => {
      const project = repo.create({ name: 'Test Project', path: '/test/path' });
      
      expect(project.id).toBeDefined();
      expect(project.name).toBe('Test Project');
      expect(project.path).toBe('/test/path');
      expect(project.createdAt).toBeDefined();
      expect(project.updatedAt).toBeDefined();
    });
  });

  describe('findById', () => {
    it('should find project by id', () => {
      const created = repo.create({ name: 'Test', path: '/test' });
      const found = repo.findById(created.id);
      
      expect(found).toEqual(created);
    });

    it('should return null for non-existent id', () => {
      expect(repo.findById('nonexistent')).toBeNull();
    });
  });

  describe('findByName', () => {
    it('should find project by name', () => {
      const created = repo.create({ name: 'Unique Name', path: '/test' });
      const found = repo.findByName('Unique Name');
      
      expect(found).toEqual(created);
    });
  });

  describe('findByPath', () => {
    it('should find project by path', () => {
      const created = repo.create({ name: 'Test', path: '/unique/path' });
      const found = repo.findByPath('/unique/path');
      
      expect(found).toEqual(created);
    });
  });

  describe('findAll', () => {
    it('should return all projects sorted by name', () => {
      repo.create({ name: 'Zebra', path: '/z' });
      repo.create({ name: 'Alpha', path: '/a' });
      repo.create({ name: 'Middle', path: '/m' });
      
      const all = repo.findAll();
      expect(all).toHaveLength(3);
      expect(all[0].name).toBe('Alpha');
      expect(all[1].name).toBe('Middle');
      expect(all[2].name).toBe('Zebra');
    });
  });

  describe('delete', () => {
    it('should delete project', () => {
      const created = repo.create({ name: 'Test', path: '/test' });
      expect(repo.delete(created.id)).toBe(true);
      expect(repo.findById(created.id)).toBeNull();
    });

    it('should return false for non-existent project', () => {
      expect(repo.delete('nonexistent')).toBe(false);
    });
  });
});

describe('IssueRepo', () => {
  let db: DatabaseManager;
  let repo: IssueRepo;
  let projectId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;
    
    repo = new IssueRepo(db);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('create', () => {
    it('should create an issue', () => {
      const issue = repo.create({
        number: 1,
        projectId,
        title: 'Test Issue',
        body: 'Test body'
      });
      
      expect(issue.number).toBe(1);
      expect(issue.title).toBe('Test Issue');
      expect(issue.body).toBe('Test body');
      expect(issue.stage).toBe(Stage.Draft);
      expect(issue.status).toBe(IssueStatus.Active);
    });
  });

  describe('findByNumber', () => {
    it('should find issue by number', () => {
      repo.create({ number: 1, projectId, title: 'Test' });
      const found = repo.findByNumber(projectId, 1);
      
      expect(found?.number).toBe(1);
    });

    it('should return null for non-existent issue', () => {
      expect(repo.findByNumber(projectId, 999)).toBeNull();
    });
  });

  describe('findByStage', () => {
    it('should find issues by stage', () => {
      repo.create({ number: 1, projectId, title: 'Draft 1' });
      repo.create({ number: 2, projectId, title: 'Draft 2' });
      
      const draftIssues = repo.findByStage(projectId, Stage.Draft);
      expect(draftIssues).toHaveLength(2);
    });
  });

  describe('updateStage', () => {
    it('should update issue stage', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const issueId = db.get<{ id: string }>('SELECT id FROM issues WHERE project_id = ? AND number = ?', [projectId, 1])?.id;
      const updated = repo.updateStage(issueId!, Stage.Designing);
      
      expect(updated?.stage).toBe(Stage.Designing);
    });
  });

  describe('updateStatus', () => {
    it('should update issue status', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const issueId = db.get<{ id: string }>('SELECT id FROM issues WHERE project_id = ? AND number = ?', [projectId, 1])?.id;
      const updated = repo.updateStatus(issueId!, IssueStatus.Paused);
      
      expect(updated?.status).toBe(IssueStatus.Paused);
    });
  });

  describe('getNextNumber', () => {
    it('should return next available number', () => {
      repo.create({ number: 1, projectId, title: 'First' });
      repo.create({ number: 3, projectId, title: 'Third' });
      
      expect(repo.getNextNumber(projectId)).toBe(4);
    });

    it('should return 1 for new project', () => {
      expect(repo.getNextNumber(projectId)).toBe(1);
    });
  });
});

describe('ConfigRepo', () => {
  let db: DatabaseManager;
  let repo: ConfigRepo;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    repo = new ConfigRepo(db);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('set/get', () => {
    it('should set and get config value', () => {
      repo.set('test.key', 'test-value');
      expect(repo.get('test.key')).toBe('test-value');
    });

    it('should return null for non-existent key', () => {
      expect(repo.get('nonexistent')).toBeNull();
    });

    it('should overwrite existing value', () => {
      repo.set('test.key', 'value1');
      repo.set('test.key', 'value2');
      expect(repo.get('test.key')).toBe('value2');
    });
  });

  describe('getNumber', () => {
    it('should return number value', () => {
      repo.set('num.key', '42');
      expect(repo.getNumber('num.key', 0)).toBe(42);
    });

    it('should return default for non-existent key', () => {
      expect(repo.getNumber('nonexistent', 100)).toBe(100);
    });
  });

  describe('delete', () => {
    it('should delete config key', () => {
      repo.set('test.key', 'value');
      expect(repo.delete('test.key')).toBe(true);
      expect(repo.get('test.key')).toBeNull();
    });
  });

  describe('getAll', () => {
    it('should return all config values', () => {
      repo.set('key1', 'value1');
      repo.set('key2', 'value2');
      
      const all = repo.getAll();
      expect(all['key1']).toBe('value1');
      expect(all['key2']).toBe('value2');
    });
  });
});

describe('IssueRepo Labels', () => {
  let db: DatabaseManager;
  let repo: IssueRepo;
  let projectId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;
    
    repo = new IssueRepo(db);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('create with labels', () => {
    it('should create an issue with labels', () => {
      const issue = repo.create({
        number: 1,
        projectId,
        title: 'Test Issue',
        labels: ['bug', 'priority:high']
      });
      
      expect(issue.labels).toEqual(['bug', 'priority:high']);
    });

    it('should default to empty labels array', () => {
      const issue = repo.create({
        number: 1,
        projectId,
        title: 'Test Issue'
      });
      
      expect(issue.labels).toEqual([]);
    });
  });

  describe('addLabel', () => {
    it('should add a label to an issue', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const updated = repo.addLabel(issue.id, 'bug');
      
      expect(updated?.labels).toContain('bug');
    });

    it('should not duplicate labels', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test', labels: ['bug'] });
      const updated = repo.addLabel(issue.id, 'bug');
      
      expect(updated?.labels).toEqual(['bug']);
    });
  });

  describe('removeLabel', () => {
    it('should remove a label from an issue', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test', labels: ['bug', 'feature'] });
      const updated = repo.removeLabel(issue.id, 'bug');
      
      expect(updated?.labels).toEqual(['feature']);
    });

    it('should handle non-existent label gracefully', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test', labels: ['bug'] });
      const updated = repo.removeLabel(issue.id, 'nonexistent');
      
      expect(updated?.labels).toEqual(['bug']);
    });
  });

  describe('update with labels', () => {
    it('should update labels via update method', () => {
      const issue = repo.create({ number: 1, projectId, title: 'Test' });
      const updated = repo.update(issue.id, { labels: ['new-label'] });
      
      expect(updated?.labels).toEqual(['new-label']);
    });
  });
});

describe('CommentRepo', () => {
  let db: DatabaseManager;
  let repo: CommentRepo;
  let issueId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    
    const issueRepo = new IssueRepo(db);
    const issue = issueRepo.create({ number: 1, projectId: project.id, title: 'Test Issue' });
    issueId = issue.id;
    
    repo = new CommentRepo(db);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('create', () => {
    it('should create a comment', () => {
      const comment = repo.create({ issueId, body: 'Test comment' });
      
      expect(comment.id).toBeDefined();
      expect(comment.issueId).toBe(issueId);
      expect(comment.body).toBe('Test comment');
      expect(comment.createdAt).toBeDefined();
    });
  });

  describe('findByIssue', () => {
    it('should find comments by issue', () => {
      repo.create({ issueId, body: 'Comment 1' });
      repo.create({ issueId, body: 'Comment 2' });
      
      const comments = repo.findByIssue(issueId);
      expect(comments).toHaveLength(2);
    });

    it('should return comments in chronological order', async () => {
      repo.create({ issueId, body: 'First' });
      await new Promise(r => setTimeout(r, 10));
      repo.create({ issueId, body: 'Second' });
      
      const comments = repo.findByIssue(issueId);
      expect(comments[0].body).toBe('First');
      expect(comments[1].body).toBe('Second');
    });

    it('should return empty array for issue with no comments', () => {
      const comments = repo.findByIssue(issueId);
      expect(comments).toEqual([]);
    });
  });

  describe('deleteByIssue', () => {
    it('should delete all comments for an issue', () => {
      repo.create({ issueId, body: 'Comment 1' });
      repo.create({ issueId, body: 'Comment 2' });
      
      const count = repo.deleteByIssue(issueId);
      expect(count).toBe(2);
      expect(repo.findByIssue(issueId)).toHaveLength(0);
    });
  });
});

describe('LabelRepo', () => {
  let db: DatabaseManager;
  let repo: LabelRepo;
  let projectId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test', path: '/test' });
    projectId = project.id;
    
    repo = new LabelRepo(db);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('findAllUsed', () => {
    it('should return all labels used in project', () => {
      const issueRepo = new IssueRepo(db);
      issueRepo.create({ number: 1, projectId, title: 'Issue 1', labels: ['bug', 'priority:high'] });
      issueRepo.create({ number: 2, projectId, title: 'Issue 2', labels: ['feature', 'bug'] });
      
      const labels = repo.findAllUsed(projectId);
      expect(labels).toEqual(['bug', 'feature', 'priority:high']);
    });

    it('should return empty array for project with no labels', () => {
      const labels = repo.findAllUsed(projectId);
      expect(labels).toEqual([]);
    });

    it('should only return labels from specified project', () => {
      const projectRepo = new ProjectRepo(db);
      const otherProject = projectRepo.create({ name: 'Other', path: '/other' });
      
      const issueRepo = new IssueRepo(db);
      issueRepo.create({ number: 1, projectId, title: 'Issue 1', labels: ['bug'] });
      issueRepo.create({ number: 1, projectId: otherProject.id, title: 'Issue 2', labels: ['feature'] });
      
      const labels = repo.findAllUsed(projectId);
      expect(labels).toEqual(['bug']);
    });
  });
});
