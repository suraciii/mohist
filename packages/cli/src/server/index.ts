import { HttpServer } from './http-server';
import { getStateManager } from './state-manager';
import { createProjectRoutes } from '../api/projects';
import { createIssueRoutes } from '../api/issues';
import { createConfigRoutes } from '../api/config';
import { createStatusRoutes } from '../api/status';
import { createLabelRoutes } from '../api/labels';
import { createEventRoutes } from '../api/events';
import { createAgentRoutes } from '../api/agent';
import { ConfigService, EventBus, AgentRunnerService, IssueService, ProjectService } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { SessionManager } from '../agent-runtime';
import type { LlmConfig } from '../agent-runtime';
import * as fs from 'fs';
import * as path from 'path';

const DEFAULT_MODEL = 'anthropic/claude-sonnet-4-20250514';

function buildLlmConfig(configRepo: { get(key: string): string | null }): LlmConfig | undefined {
  const model = configRepo.get('llm.model');
  const modelStr = model ?? DEFAULT_MODEL;
  const slashIndex = modelStr.indexOf('/');
  if (slashIndex === -1) return model ? { model } : undefined;

  const providerID = modelStr.slice(0, slashIndex);
  const baseURLKey = `llm.provider.${providerID}.options.baseURL`;
  const baseURL = configRepo.get(baseURLKey);

  const config: LlmConfig = {};
  if (model) config.model = model;
  if (baseURL) {
    config.provider = {
      [providerID]: { options: { baseURL } },
    };
  }

  if (config.model || config.provider) return config;
  return undefined;
}

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

  const issueService = new IssueService(stateManager.getIssueRepo(), stateManager.getCommentRepo());
  const projectService = new ProjectService(stateManager.getProjectRepo(), stateManager.getConfigRepo(), stateManager.getIssueRepo(), stateManager.getLabelRepo());

  const worktreeManager = new WorktreeManager();
  const sessionManager = new SessionManager();
  const eventBus = new EventBus();
  const agentRunner = new AgentRunnerService(eventBus);

  const llmConfig = buildLlmConfig(stateManager.getConfigRepo());
  
  const server = new HttpServer(config);
  
  server.addRouter('/api/projects', createProjectRoutes(projectService));
  server.addRouter('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, sessionManager, llmConfig, agentRunner));
  server.addRouter('/api/labels', createLabelRoutes(projectService));
  server.addRouter('/api/config', createConfigRoutes(configService));
  server.addRouter('/api', createStatusRoutes(projectService, issueService));
  server.addRouter('/api/events', createEventRoutes(eventBus));
  server.addRouter('/api/agent', createAgentRoutes(agentRunner));

  const webDistDir = path.join(__dirname, '..', '..', 'web', 'dist');
  server.serveStaticFiles(webDistDir);

  process.on('SIGTERM', async () => {
    console.log('Received SIGTERM, shutting down gracefully...');
    await server.stop();
  });

  process.on('SIGINT', async () => {
    console.log('Received SIGINT, shutting down gracefully...');
    await server.stop();
  });

  process.on('unhandledRejection', (reason, _promise) => {
    console.error('[FATAL] Unhandled Promise Rejection:', reason);
    if (reason instanceof Error) {
      console.error('Stack trace:', reason.stack);
    }
  });

  process.on('uncaughtException', (error) => {
    console.error('[FATAL] Uncaught Exception:', error.message);
    console.error('Stack trace:', error.stack);
  });

  await server.start();
  
  console.log(`mohist server started on port ${config.serverPort}`);
  console.log(`Max concurrent agents: ${config.maxConcurrentAgents}`);
}

main().catch((error) => {
  console.error('Failed to start server:', error);
  process.exit(1);
});
