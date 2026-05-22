import { HttpServer } from './http-server';
import { StateManager } from './state-manager';
import { DatabaseManager } from '../db';
import { createProjectRoutes } from '../api/projects';
import { createIssueRoutes } from '../api/issues';
import { createWorkflowRoutes } from '../api/issues/workflow-routes';
import { createEpicRoutes } from '../api/epics';
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
import { createOpencodeModelsRoutes } from '../api/opencode-models';
import { createScheduleRoutes } from '../api/schedules';
import { createSettingsConfigRoutes } from '../api/settings-config';
import { createSettingsSystemRoutes } from '../api/settings-system';
import { ConfigService, EventBus, AgentRunnerService, IssueService, ProjectService, ExploreService, ExploreAcpService, SchedulerService, WorkflowRunService, IssuePrerequisiteService, EpicService, type SkillRunner, type ConflictResolutionDeps } from '../services';
import { ProviderStateService } from '../services/provider-state-service';
import { WorktreeManager } from '../git/worktree-manager';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { Log } from '../util/log';
import { getVersionInfo } from '../version';


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
  const epicService = new EpicService(stateManager.getEpicRepo(), stateManager.getIssueRepo());

  const worktreeManager = new WorktreeManager();
  const eventBus = new EventBus();
  const workflowLogRepo = stateManager.getWorkflowLogRepo();
  const sessionStreamLogRepo = stateManager.getSessionStreamLogRepo();
  const conflictResolutionDeps: ConflictResolutionDeps = {
    issueRepo: stateManager.getIssueRepo(),
    workflowLogRepo,
    sessionStreamLogRepo,
    coderSessionRepo: stateManager.getCoderSessionRepo(),
    eventBus,
    opencodeBinPath,
  };

  const coderSessionRepo = stateManager.getCoderSessionRepo();

  const stageStateService: any = null;
  const workflowRunService = new WorkflowRunService(db);

  const issuePrerequisiteService = new IssuePrerequisiteService(
    stateManager.getIssueRepo(),
    stateManager.getIssueStartPrerequisiteRepo(),
  );

  const agentRunner = new AgentRunnerService(eventBus, workflowLogRepo, stateManager.getIssueRepo(), configService.getMaxConcurrentAgents(), stateManager.getCoderSessionRepo(), stateManager.getPipelineCheckpointRepo(), stateManager.getProjectRepo(), worktreeManager, stateManager.getIssueTaskQueueRepo(), conflictResolutionDeps, sessionStreamLogRepo, stateManager.getStageExecutionRepo(), stageStateService, workflowRunService, issuePrerequisiteService);

  agentRunner.setLlmConfig(fileConfig);

  agentRunner.recoverFromQueue();
  agentRunner.recoverIssues();

  const expiredCount = stateManager.getQuestionRepo().expireAllPending();
  if (expiredCount > 0) {
    log.info(`Expired ${expiredCount} orphaned pending question(s) from previous session`);
  }

  const skillRunner: SkillRunner = {
    async runSkill(skillName: string): Promise<void> {
      log.warn('SkillRunner.runSkill called but SkillService is not yet available', { skillName });
    },
  };

  const scheduler = new SchedulerService(
    stateManager.getScheduleRepo(),
    skillRunner,
    eventBus,
  );

  scheduler.start();

  const rateLimiter = new RateLimiter(60 * 1000, 30);
  const server = new HttpServer(config, rateLimiter);

  const providerState = new ProviderStateService();
  await providerState.warm();

  server.addRouter('/api/projects', createProjectRoutes(projectService));
  server.addRouter('/api/epics', createEpicRoutes(epicService, projectService));
  server.addRouter('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, fileConfig, agentRunner, workflowLogRepo, sessionStreamLogRepo, stateManager.getCoderSessionRepo(), opencodeBinPath, stateManager.getPipelineCheckpointRepo(), undefined, stateManager.getCheckSuiteRepo(), stateManager.getStageExecutionRepo(), stageStateService, workflowRunService, issuePrerequisiteService, epicService));
  server.addRouter('/api/issues', createWorkflowRoutes(issueService, projectService, stateManager, worktreeManager, fileConfig, agentRunner, workflowLogRepo, sessionStreamLogRepo, stateManager.getCoderSessionRepo(), opencodeBinPath, stateManager.getPipelineCheckpointRepo(), undefined, stateManager.getCheckSuiteRepo(), stateManager.getStageExecutionRepo(), stageStateService, workflowRunService, issuePrerequisiteService, epicService));
  server.addRouter('/api/propose', createProposeRoutes(issueService, projectService, stateManager, worktreeManager, fileConfig, agentRunner, opencodeBinPath));
  server.addRouter('/api/questions', createQuestionRoutes(stateManager.getQuestionRepo(), stateManager.getIssueRepo(), eventBus));
  server.addRouter('/api/labels', createLabelRoutes(projectService));
  server.addRouter('/api/config', createConfigRoutes(configService));
  server.addRouter('/api/providers', createProviderRoutes(eventBus, rateLimiter, providerState));
  server.addRouter('/api', createStatusRoutes(projectService, issueService, fileConfig, getVersionInfo()));
  server.addRouter('/api/events', createEventRoutes(eventBus));
  server.addRouter('/api/agent', createAgentRoutes(agentRunner, coderSessionRepo, projectService));
  server.addRouter('/api/opencode', createOpencodeModelsRoutes());
  server.addRouter('/api/fs', createFsRoutes());
  server.addRouter('/api/explore', createExploreRoutes(exploreService, issueService, projectService, stateManager.getExploreSessionRepo(), eventBus, (projectPath: string) => {
    return new ExploreAcpService({
      worktreePath: projectPath,
      issueService,
      artifactManager: new ChangeArtifactsManager(projectPath),
    });
  }));
  server.addRouter('/api/logs', createLogRoutes());
  server.addRouter('/api/agent/schedules', createScheduleRoutes(stateManager.getScheduleRepo(), scheduler));
  server.addRouter('/api', createSettingsConfigRoutes({ host: config.serverHost, port: config.serverPort }));
  server.addRouter('/api/settings/system', createSettingsSystemRoutes());

  eventBus.on('agent_completed', async ({ issueNumber }) => {
    log.info('Agent completed', { issueNumber });
  });

  const webDistDir = path.join(__dirname, '..', '..', 'web', 'dist');
  server.serveStaticFiles(webDistDir);

  process.on('SIGTERM', async () => {
    log.info('Received SIGTERM, shutting down gracefully...');
    scheduler.stop();
    agentRunner.shutdown();
    await server.stop();
    process.exit(143);
  });

  process.on('SIGINT', async () => {
    log.info('Received SIGINT, shutting down gracefully...');
    scheduler.stop();
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
