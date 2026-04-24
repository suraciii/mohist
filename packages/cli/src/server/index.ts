import { HttpServer } from './http-server';
import { StateManager } from './state-manager';
import { DatabaseManager } from '../db';
import { createProjectRoutes } from '../api/projects';
import { createIssueRoutes } from '../api/issues';
import { createProposeRoutes } from '../api/propose';
import { createConfigRoutes } from '../api/config';
import { createProviderRoutes } from '../api/providers';
import { createStatusRoutes } from '../api/status';
import { createLabelRoutes } from '../api/labels';
import { createEventRoutes } from '../api/events';
import { createAgentRoutes } from '../api/agent';
import { createFsRoutes } from '../api/fs';
import { createQuestionRoutes } from '../api/questions';
import { createExploreRoutes } from '../api/explore';
import { createLogRoutes } from '../api/logs';
import { ConfigService, EventBus, AgentRunnerService, IssueService, ProjectService, ExploreService, ExploreAcpService } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { SessionManager } from '../agent-runtime';
import { Log } from '../util/log';

import { load as loadConfig, getServerConfig, getLogConfig, resolveOpencodeBinPath } from '../config/config-loader';
import { RateLimiter } from '../utils/rate-limiter';
import * as fs from 'fs';
import * as path from 'path';

const log = Log.create({ service: 'server' });

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

  process.on('unhandledRejection', (reason) => {
    console.error('[FATAL] Unhandled Promise Rejection (pre-init):', reason);
  });

  let logLevel: Log.Level = 'INFO';
  if (process.env.LOG_LEVEL) {
    logLevel = process.env.LOG_LEVEL as Log.Level;
  }

  await Log.init({
    print: process.argv.includes('--print-logs'),
    dev: process.env.NODE_ENV === 'development',
    level: logLevel,
  });

  process.removeAllListeners('unhandledRejection');
  process.on('unhandledRejection', (reason) => {
    Log.Default.error('Unhandled Promise Rejection', { reason });
  });

  let fileConfig: ReturnType<typeof loadConfig>;
  try {
    fileConfig = loadConfig();
  } catch (err) {
    log.error('Failed to load config', { error: err instanceof Error ? err.message : err });
    process.exit(1);
  }

  const logConfig = getLogConfig(fileConfig);
  if (logConfig.level !== logLevel) {
    log.info('Overriding log level from config', { from: logLevel, to: logConfig.level });
  }
  
  const serverConfig = getServerConfig(fileConfig);

  const opencodeBinPath = resolveOpencodeBinPath(fileConfig);
  if (opencodeBinPath) {
    log.info('Resolved opencode binary path', { path: opencodeBinPath });
  } else {
    log.warn('Could not resolve opencode binary path; will fall back to PATH lookup');
  }
  
  const db = new DatabaseManager();
  const stateManager = new StateManager(db);
  const configService = new ConfigService(stateManager.getConfigRepo());
  const dbConfig = configService.getConfig();
  
  // Merge: JSONC server config overrides DB config
  const config = {
    ...dbConfig,
    serverPort: serverConfig.port,
    serverHost: serverConfig.host,
  };

  const issueService = new IssueService(stateManager.getIssueRepo(), stateManager.getCommentRepo());
  const projectService = new ProjectService(stateManager.getProjectRepo(), stateManager.getConfigRepo(), stateManager.getIssueRepo(), stateManager.getLabelRepo());
  const exploreService = new ExploreService(stateManager.getExploreSessionRepo(), stateManager.getExploreMessageRepo());

  const worktreeManager = new WorktreeManager();
  const sessionManager = new SessionManager();
  const eventBus = new EventBus();
  const workflowLogRepo = stateManager.getWorkflowLogRepo();
  const agentRunner = new AgentRunnerService(eventBus, workflowLogRepo, stateManager.getIssueRepo(), configService.getMaxConcurrentAgents(), stateManager.getAgentSessionMessageRepo(), stateManager.getCoderSessionRepo());

  agentRunner.setLlmConfig(fileConfig);

  agentRunner.recoverIssues();

  const expiredCount = stateManager.getQuestionRepo().expireAllPending();
  if (expiredCount > 0) {
    log.info(`Expired ${expiredCount} orphaned pending question(s) from previous session`);
  }

  const rateLimiter = new RateLimiter(60 * 1000, 30);
  const server = new HttpServer(config, rateLimiter);
  
  server.addRouter('/api/projects', createProjectRoutes(projectService));
  server.addRouter('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, sessionManager, fileConfig, agentRunner, workflowLogRepo, stateManager.getAgentSessionMessageRepo(), stateManager.getCoderSessionRepo(), opencodeBinPath));
  server.addRouter('/api/propose', createProposeRoutes(issueService, projectService, stateManager, worktreeManager, sessionManager, fileConfig, agentRunner, opencodeBinPath));
  server.addRouter('/api/questions', createQuestionRoutes(stateManager.getQuestionRepo(), stateManager.getIssueRepo(), eventBus));
  server.addRouter('/api/labels', createLabelRoutes(projectService));
  server.addRouter('/api/config', createConfigRoutes(configService));
  server.addRouter('/api/providers', createProviderRoutes(eventBus, rateLimiter));
  server.addRouter('/api', createStatusRoutes(projectService, issueService, fileConfig));
  server.addRouter('/api/events', createEventRoutes(eventBus));
  server.addRouter('/api/agent', createAgentRoutes(agentRunner));
  server.addRouter('/api/fs', createFsRoutes());
  server.addRouter('/api/explore', createExploreRoutes(exploreService, issueService, projectService, stateManager.getExploreSessionRepo(), eventBus, (projectPath: string) => {
    return new ExploreAcpService({
      worktreePath: projectPath,
      issueService,
      artifactManager: new ChangeArtifactsManager(projectPath),
    });
  }));
  server.addRouter('/api/logs', createLogRoutes());

  eventBus.on('agent_completed', async ({ issueNumber, projectId }) => {
    try {
      const project = projectService.getById(projectId);
      if (!project || !worktreeManager) return;

      if (!worktreeManager.exists(project.name, issueNumber)) return;

      log.info('Auto-merging completed issue back to base branch', { issueNumber, projectId, baseBranch: project.baseBranch });
      const result = await worktreeManager.mergeBack(project.path, project.name, issueNumber, project.baseBranch);
      if (result.success) {
        log.info('Auto-merge succeeded', { issueNumber, message: result.message });
      } else {
        log.warn('Auto-merge failed, manual merge required', { issueNumber, message: result.message });
      }
    } catch (err) {
      log.error('Auto-merge error', { issueNumber, error: err instanceof Error ? err.message : String(err) });
    }
  });

  const webDistDir = path.join(__dirname, '..', '..', 'web', 'dist');
  server.serveStaticFiles(webDistDir);

  process.on('SIGTERM', async () => {
    log.info('Received SIGTERM, shutting down gracefully...');
    agentRunner.shutdown();
    await server.stop();
  });

  process.on('SIGINT', async () => {
    log.info('Received SIGINT, shutting down gracefully...');
    agentRunner.shutdown();
    await server.stop();
  });

  process.on('uncaughtException', (error) => {
    log.error('Uncaught Exception', { error });
  });

  await server.start();
  
  log.info(`mohist server started`, {
    host: config.serverHost,
    port: config.serverPort,
    maxConcurrentAgents: config.maxConcurrentAgents,
  });
}

main().catch((error) => {
  log.error('Failed to start server', { error });
  process.exit(1);
});
