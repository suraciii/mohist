import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { DatabaseManager } from '../src/db/database';
import { initializeDatabase } from '../src/db/migrations';
import { ProjectRepo } from '../src/db/project-repo';
import { IssueRepo } from '../src/db/issue-repo';
import { AgentRunnerService } from '../src/services/agent-runner-service';
import { EventBus } from '../src/services/event-bus';
import { Stage, IssueStatus } from '../src/types';

vi.mock('../src/agent-runtime/acp-session', () => ({
  createAcpConnection: vi.fn().mockRejectedValue(
    new Error('[SPAWN_FAILED] spawn opencode ENOENT')
  ),
  runAcpSession: vi.fn().mockResolvedValue({
    text: '',
    success: false,
    error: '[SPAWN_FAILED] spawn opencode ENOENT',
  }),
  truncateAgentText: vi.fn((s: string) => s),
}));

describe('End-to-end spawn failure rollback', () => {
  let db: DatabaseManager;
  let projectRepo: ProjectRepo;
  let issueRepo: IssueRepo;
  let eventBus: EventBus;
  let tmpDir: string;

  beforeEach(() => {
    db = new DatabaseManager({ inMemory: true });
    initializeDatabase(db);
    projectRepo = new ProjectRepo(db);
    issueRepo = new IssueRepo(db);
    eventBus = new EventBus();
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-e2e-'));
  });

  afterEach(() => {
    db.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('should block issue and rollback stage to draft on spawn failure', async () => {
    const project = projectRepo.create({ name: 'Test', path: tmpDir });
    const issue = issueRepo.create({
      number: 1,
      projectId: project.id,
      title: 'Spawn Fail Test',
    });

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    const agentErrorEvents: Array<{ issueId: string; projectId: string; error: string }> = [];
    eventBus.on('agent_error', (data) => {
      agentErrorEvents.push(data);
    });

    const result = service.startPipeline(
      issue,
      project.id,
      issueRepo,
      tmpDir,
      { cwd: tmpDir },
      (issueId, status) => { issueRepo.updateStatus(issueId, status); },
    );

    expect(result.started).toBe(true);

    const activeAgents = (service as any).activeAgents as Map<string, any>;
    const agent = activeAgents.get(issue.id);
    expect(agent).toBeDefined();
    await agent.promise;

    const updated = issueRepo.findById(issue.id);
    expect(updated?.status).toBe(IssueStatus.Blocked);
    expect(updated?.stage).toBe(Stage.Draft);
    expect(activeAgents.has(issue.id)).toBe(false);
    expect(agentErrorEvents.length).toBe(1);
    expect(agentErrorEvents[0].issueId).toBe(issue.id);
    expect(agentErrorEvents[0].error).toContain('SPAWN_FAILED');
  });

  it('should clean up activeAgents even if status update fails', async () => {
    const project = projectRepo.create({ name: 'Test', path: tmpDir });
    const issue = issueRepo.create({
      number: 2,
      projectId: project.id,
      title: 'Spawn Fail DB Error',
    });

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    const result = service.startPipeline(
      issue,
      project.id,
      issueRepo,
      tmpDir,
      { cwd: tmpDir },
      (_issueId, _status) => { throw new Error('DB connection lost'); },
    );

    expect(result.started).toBe(true);

    const activeAgents = (service as any).activeAgents as Map<string, any>;
    const agent = activeAgents.get(issue.id);
    expect(agent).toBeDefined();
    await agent.promise;

    expect(activeAgents.has(issue.id)).toBe(false);
  });

  it('should emit agent_error even when updateIssueStatus throws', async () => {
    const project = projectRepo.create({ name: 'Test', path: tmpDir });
    const issue = issueRepo.create({
      number: 3,
      projectId: project.id,
      title: 'Spawn Fail Emit Check',
    });

    const service = new AgentRunnerService(eventBus, undefined, issueRepo, 8);

    const agentErrorEvents: Array<{ issueId: string; projectId: string; error: string }> = [];
    eventBus.on('agent_error', (data) => {
      agentErrorEvents.push(data);
    });

    service.startPipeline(
      issue,
      project.id,
      issueRepo,
      tmpDir,
      { cwd: tmpDir },
      () => { throw new Error('DB down'); },
    );

    const activeAgents = (service as any).activeAgents as Map<string, any>;
    const agent = activeAgents.get(issue.id);
    if (agent) {
      await agent.promise;
    }

    expect(agentErrorEvents.length).toBe(1);
    expect(agentErrorEvents[0].issueId).toBe(issue.id);
  });
});
