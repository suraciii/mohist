import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { resetDatabase, closeDatabase } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { DatabaseManager } from '../src/db/database';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { ConfigRepo } from '../src/db/config-repo';
import { ProjectService } from '../src/services/project-service';
import { IssueService } from '../src/services/issue-service';
import { ConfigService } from '../src/services/config-service';
import { Stage, IssueStatus } from '../src/types';

describe('ProjectService', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let configRepo: ConfigRepo;
  let service: ProjectService;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    configRepo = new ConfigRepo(db);
    service = new ProjectService(projectRepo, configRepo, issueRepo);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('create', () => {
    it('should create a project', () => {
      const project = service.create({ name: 'Test Project', path: '/test/path' });
      
      expect(project.id).toBeDefined();
      expect(project.name).toBe('Test Project');
      expect(project.path).toBe('/test/path');
    });

    it('should throw on duplicate name', () => {
      service.create({ name: 'Test', path: '/path1' });
      
      expect(() => service.create({ name: 'Test', path: '/path2' }))
        .toThrow('already exists');
    });

    it('should throw on duplicate path', () => {
      service.create({ name: 'Project1', path: '/test/path' });
      
      expect(() => service.create({ name: 'Project2', path: '/test/path' }))
        .toThrow('already used');
    });
  });

  describe('getById/getByName/getByPath', () => {
    it('should find project by id', () => {
      const created = service.create({ name: 'Test', path: '/test' });
      const found = service.getById(created.id);
      
      expect(found?.name).toBe('Test');
    });

    it('should find project by name', () => {
      service.create({ name: 'Unique Name', path: '/test' });
      const found = service.getByName('Unique Name');
      
      expect(found?.path).toBe('/test');
    });

    it('should find project by path', () => {
      service.create({ name: 'Test', path: '/unique/path' });
      const found = service.getByPath('/unique/path');
      
      expect(found?.name).toBe('Test');
    });
  });

  describe('current project', () => {
    it('should have no current project initially', () => {
      expect(service.getCurrent()).toBeNull();
    });

    it('should set current project', () => {
      const project = service.create({ name: 'Test', path: '/test' });
      service.setCurrent(project);
      
      expect(service.getCurrent()?.id).toBe(project.id);
    });

    it('should set current project by name', () => {
      service.create({ name: 'Test', path: '/test' });
      const set = service.setCurrentByName('Test');
      
      expect(set?.name).toBe('Test');
      expect(service.getCurrent()?.name).toBe('Test');
    });

    it('should clear current project', () => {
      const project = service.create({ name: 'Test', path: '/test' });
      service.setCurrent(project);
      service.clearCurrent();
      
      expect(service.getCurrent()).toBeNull();
    });

    it('should clear current project on delete', () => {
      const project = service.create({ name: 'Test', path: '/test' });
      service.setCurrent(project);
      service.delete(project.id);
      
      expect(service.getCurrent()).toBeNull();
    });
  });

  describe('delete', () => {
    it('should delete project', () => {
      const project = service.create({ name: 'Test', path: '/test' });
      expect(service.delete(project.id)).toBe(true);
      expect(service.getById(project.id)).toBeNull();
    });

    it('should return false for non-existent project', () => {
      expect(service.delete('nonexistent')).toBe(false);
    });

    it('should delete by name', () => {
      service.create({ name: 'Test', path: '/test' });
      expect(service.deleteByName('Test')).toBe(true);
      expect(service.getByName('Test')).toBeNull();
    });
  });

  describe('getAll', () => {
    it('should return all projects sorted by name', () => {
      service.create({ name: 'Zebra', path: '/z' });
      service.create({ name: 'Alpha', path: '/a' });
      
      const all = service.getAll();
      expect(all).toHaveLength(2);
      expect(all[0].name).toBe('Alpha');
      expect(all[1].name).toBe('Zebra');
    });
  });

  describe('exists', () => {
    it('should return true for existing project', () => {
      service.create({ name: 'Test', path: '/test' });
      expect(service.exists('Test')).toBe(true);
    });

    it('should return false for non-existing project', () => {
      expect(service.exists('NonExistent')).toBe(false);
    });
  });
});

describe('IssueService', () => {
  let db: DatabaseManager;
  let issueRepo: IssueRepo;
  let service: IssueService;
  let projectId: string;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    const projectRepo = new ProjectRepo(db);
    const project = projectRepo.create({ name: 'Test Project', path: '/test' });
    projectId = project.id;
    
    issueRepo = new IssueRepo(db);
    service = new IssueService(issueRepo);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('create', () => {
    it('should create an issue with auto-incrementing number', () => {
      const issue1 = service.create({ projectId, title: 'First' });
      const issue2 = service.create({ projectId, title: 'Second' });
      
      expect(issue1.number).toBe(1);
      expect(issue2.number).toBe(2);
    });

    it('should create issue in draft stage', () => {
      const issue = service.create({ projectId, title: 'Test' });
      
      expect(issue.stage).toBe(Stage.Draft);
      expect(issue.status).toBe(IssueStatus.Active);
    });
  });

  describe('getByNumber', () => {
    it('should find issue by number', () => {
      service.create({ projectId, title: 'Test' });
      const found = service.getByNumber(projectId, 1);
      
      expect(found?.number).toBe(1);
    });
  });

  describe('transitionToStageByNumber', () => {
    it('should transition issue stage', () => {
      service.create({ projectId, title: 'Test' });
      const updated = service.transitionToStageByNumber(projectId, 1, Stage.Plan);
      
      expect(updated?.stage).toBe(Stage.Plan);
    });

    it('should return null for non-existent issue', () => {
      const result = service.transitionToStageByNumber(projectId, 999, Stage.Plan);
      expect(result).toBeNull();
    });
  });

  describe('pause/resume', () => {
    it('should pause issue', () => {
      service.create({ projectId, title: 'Test' });
      const paused = service.pause(projectId, 1);
      
      expect(paused?.status).toBe(IssueStatus.Paused);
    });

    it('should resume paused issue', () => {
      service.create({ projectId, title: 'Test' });
      service.pause(projectId, 1);
      const resumed = service.resume(projectId, 1);
      
      expect(resumed?.status).toBe(IssueStatus.Active);
    });
  });

  describe('block', () => {
    it('should block issue', () => {
      service.create({ projectId, title: 'Test' });
      const blocked = service.block(projectId, 1);
      
      expect(blocked?.status).toBe(IssueStatus.Blocked);
    });
  });

  describe('getByStage', () => {
    it('should filter issues by stage', () => {
      service.create({ projectId, title: 'Draft 1' });
      service.create({ projectId, title: 'Draft 2' });
      service.transitionToStageByNumber(projectId, 1, Stage.Plan);
      
      const plan = service.getByStage(projectId, Stage.Plan);
      const drafts = service.getByStage(projectId, Stage.Draft);
      
      expect(plan).toHaveLength(1);
      expect(drafts).toHaveLength(1);
    });
  });
});

describe('ConfigService', () => {
  let db: DatabaseManager;
  let configRepo: ConfigRepo;
  let service: ConfigService;

  beforeEach(() => {
    db = resetDatabase({ inMemory: true });
    initializeDatabase(db);
    
    configRepo = new ConfigRepo(db);
    service = new ConfigService(configRepo);
  });

  afterEach(() => {
    closeDatabase();
  });

  describe('getConfig', () => {
    it('should return default config', () => {
      const config = service.getConfig();
      
      expect(config.serverPort).toBe(3456);
      expect(config.agentTimeout).toBe(1800000);
      expect(config.maxConcurrentAgents).toBe(8);
      expect(config.pollInterval).toBe(30000);
    });
  });

  describe('set/get', () => {
    it('should set and get config value', () => {
      service.set('test.key', 'test-value');
      expect(service.get('test.key')).toBe('test-value');
    });
  });

  describe('server port', () => {
    it('should set and get server port', () => {
      service.setServerPort(4000);
      expect(service.getServerPort()).toBe(4000);
    });
  });

  describe('agent timeout', () => {
    it('should set and get agent timeout', () => {
      service.setAgentTimeout(3000000);
      expect(service.getAgentTimeout()).toBe(3000000);
    });
  });

  describe('max concurrent agents', () => {
    it('should set and get max concurrent agents', () => {
      service.setMaxConcurrentAgents(4);
      expect(service.getMaxConcurrentAgents()).toBe(4);
    });
  });

  describe('validate', () => {
    it('should validate port range', () => {
      expect(service.validate('server.port', '8080').valid).toBe(true);
      expect(service.validate('server.port', '99999').valid).toBe(false);
      expect(service.validate('server.port', '0').valid).toBe(false);
    });

    it('should validate agent timeout minimum', () => {
      expect(service.validate('agent.timeout', '60000').valid).toBe(true);
      expect(service.validate('agent.timeout', '59999').valid).toBe(false);
    });

    it('should validate max concurrent agents range', () => {
      expect(service.validate('agent.maxConcurrent', '8').valid).toBe(true);
      expect(service.validate('agent.maxConcurrent', '0').valid).toBe(false);
      expect(service.validate('agent.maxConcurrent', '17').valid).toBe(false);
    });

    it('should validate poll interval minimum', () => {
      expect(service.validate('poll.interval', '5000').valid).toBe(true);
      expect(service.validate('poll.interval', '4999').valid).toBe(false);
    });
  });

  describe('resetToDefaults', () => {
    it('should reset config to defaults', () => {
      service.setServerPort(9999);
      service.resetToDefaults();
      
      expect(service.getServerPort()).toBe(3456);
    });
  });
});
