import { HttpServer } from './http-server';
import { getStateManager } from './state-manager';
import { createProjectRoutes } from '../api/projects';
import { createIssueRoutes } from '../api/issues';
import { createConfigRoutes } from '../api/config';
import { createStatusRoutes } from '../api/status';
import { createLabelRoutes } from '../api/labels';
import { ConfigService } from '../services';
import { WorkflowEngine } from '../workflow/engine';
import { AgentRunner } from '../agent/runner';
import { WorktreeManager } from '../git/worktree-manager';
import * as fs from 'fs';
import * as path from 'path';

function ensureDataDir(): void {
  const dataDir = path.join(process.env.HOME || '', '.mohist');
  
  if (!fs.existsSync(dataDir)) {
    fs.mkdirSync(dataDir, { recursive: true });
  }
  
  const projectsDir = path.join(dataDir, 'projects');
  if (!fs.existsSync(projectsDir)) {
    fs.mkdirSync(projectsDir, { recursive: true });
  }
}

async function main(): Promise<void> {
  ensureDataDir();
  
  const stateManager = getStateManager();
  const configService = new ConfigService(stateManager.getConfigRepo());
  const config = configService.getConfig();

  const worktreeManager = new WorktreeManager();
  const agentRunner = new AgentRunner(config.agentTimeout);
  const engine = new WorkflowEngine(
    stateManager.getTaskRepo(),
    stateManager.getIssueRepo(),
    stateManager.getProjectRepo(),
    agentRunner,
    worktreeManager,
    {
      maxConcurrentAgents: config.maxConcurrentAgents,
      pollInterval: config.pollInterval,
    }
  );
  
  const server = new HttpServer(config);
  
  server.addRouter('/api/projects', createProjectRoutes(stateManager));
  server.addRouter('/api/issues', createIssueRoutes(stateManager, engine, worktreeManager));
  server.addRouter('/api/labels', createLabelRoutes(stateManager));
  server.addRouter('/api/config', createConfigRoutes(configService));
  server.addRouter('/api', createStatusRoutes(stateManager, engine));

  process.on('SIGTERM', async () => {
    console.log('Received SIGTERM, shutting down gracefully...');
    await engine.stop();
    await server.stop();
    process.exit(0);
  });

  process.on('SIGINT', async () => {
    console.log('Received SIGINT, shutting down gracefully...');
    await engine.stop();
    await server.stop();
    process.exit(0);
  });

  await server.start();
  await engine.start();
  
  const { projects, activeTasks } = stateManager.recoverState();
  
  console.log(`mohist server started on port ${config.serverPort}`);
  console.log(`Max concurrent agents: ${config.maxConcurrentAgents}`);
  console.log(`Loaded ${projects.length} projects`);
  if (activeTasks.length > 0) {
    console.log(`Recovered ${activeTasks.length} tasks`);
  }
}

main().catch((error) => {
  console.error('Failed to start server:', error);
  process.exit(1);
});
