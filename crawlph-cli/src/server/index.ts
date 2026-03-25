import { HttpServer } from './http-server';
import { getStateManager } from './state-manager';
import { TaskQueue } from './task-queue';
import { createProjectRoutes } from '../api/projects';
import { createIssueRoutes } from '../api/issues';
import { createConfigRoutes } from '../api/config';
import { createStatusRoutes } from '../api/status';
import { createLabelRoutes } from '../api/labels';
import { ConfigService } from '../services';
import * as fs from 'fs';
import * as path from 'path';

function ensureDataDir(): void {
  const dataDir = path.join(process.env.HOME || '', '.crawlph');
  
  if (!fs.existsSync(dataDir)) {
    fs.mkdirSync(dataDir, { recursive: true });
  }
  
  const logsDir = path.join(dataDir, 'logs');
  if (!fs.existsSync(logsDir)) {
    fs.mkdirSync(logsDir, { recursive: true });
  }
}

async function main(): Promise<void> {
  ensureDataDir();
  
  const stateManager = getStateManager();
  const configService = new ConfigService(stateManager.getConfigRepo());
  const config = configService.getConfig();
  
  const server = new HttpServer(config);
  
  const taskQueue = new TaskQueue(config.maxConcurrentAgents);
  
  server.addRouter('/api/projects', createProjectRoutes(stateManager));
  server.addRouter('/api/issues', createIssueRoutes(stateManager, taskQueue));
  server.addRouter('/api/labels', createLabelRoutes(stateManager));
  server.addRouter('/api/config', createConfigRoutes(configService));
  server.addRouter('/api', createStatusRoutes(stateManager, taskQueue));

  process.on('SIGTERM', async () => {
    console.log('Received SIGTERM, shutting down gracefully...');
    await server.stop();
    process.exit(0);
  });

  process.on('SIGINT', async () => {
    console.log('Received SIGINT, shutting down gracefully...');
    await server.stop();
    process.exit(0);
  });

  await server.start();
  
  const { projects, activeTasks } = stateManager.recoverState();
  
  console.log(`crawlph server started on port ${config.serverPort}`);
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
