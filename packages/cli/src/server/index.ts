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
import { createOpencodeModelsRoutes } from '../api/opencode-models';
import { createScheduleRoutes } from '../api/schedules';
import { createSettingsConfigRoutes } from '../api/settings-config';
import { createSettingsSystemRoutes } from '../api/settings-system';
import { ConfigService, EventBus, AgentRunnerService, IssueService, ProjectService, ExploreService, ExploreAcpService, SchedulerService, resolveConflictsViaAgent, type SkillRunner, type ConflictResolutionDeps } from '../services';
import { WorktreeManager } from '../git/worktree-manager';
import { MergeQueue } from '../git/merge-queue';
import { Stage, IssueStatus, MergeState } from '../types';
import { ChangeArtifactsManager } from '../artifacts/change-artifacts-manager';
import { AgentSession, type AgentSessionOptions } from '../agent-runtime/agent-session';
import type { MergeEntry } from '../git/merge-queue';
import { Log } from '../util/log';
import { getVersionInfo } from '../version';


import { load as loadConfig, getServerConfig, getLogConfig, resolveOpencodeBinPath } from '../config/config-loader';
import { resolveStageModel } from '../config/model-resolution';
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

  const agentRunner = new AgentRunnerService(eventBus, workflowLogRepo, stateManager.getIssueRepo(), configService.getMaxConcurrentAgents(), stateManager.getCoderSessionRepo(), stateManager.getPipelineCheckpointRepo(), stateManager.getProjectRepo(), worktreeManager, stateManager.getIssueTaskQueueRepo(), conflictResolutionDeps, sessionStreamLogRepo, stateManager.getStageExecutionRepo());

  agentRunner.setLlmConfig(fileConfig);

  agentRunner.recoverFromQueue();
  agentRunner.recoverIssues();

  const expiredCount = stateManager.getQuestionRepo().expireAllPending();
  if (expiredCount > 0) {
    log.info(`Expired ${expiredCount} orphaned pending question(s) from previous session`);
  }

  const issueRepo = stateManager.getIssueRepo();
  const coderSessionRepo = stateManager.getCoderSessionRepo();

  const mergeQueue = new MergeQueue({
    worktreeManager,
    eventBus,
    issueRepo,
    getProjectPath: (projectId: string) => {
      const project = projectService.getById(projectId);
      if (!project) return null;
      return { path: project.path, name: project.name, baseBranch: project.baseBranch };
    },
    resolveConflicts: async (entry, worktreePath, conflictFiles) => {
      log.info('resolveConflicts callback invoked', { issueNumber: entry.issueNumber, conflictFiles });
      return resolveConflictsViaAgent(conflictResolutionDeps, entry.issueId, entry.projectId, worktreePath, conflictFiles);
    },
    fixBuildErrors: async (entry: MergeEntry, worktreePath: string, buildOutput: string) => {
      log.info('fixBuildErrors callback invoked', { issueNumber: entry.issueNumber });

      const refreshedIssue = issueRepo.findById(entry.issueId);
      if (!refreshedIssue) {
        return { success: false, error: 'Issue not found for build fix' };
      }

      const config = loadConfig();

      const acpOptions: AgentSessionOptions = {
        cwd: worktreePath,
        issueId: refreshedIssue.id,
        projectId: entry.projectId,
        workflowLogRepo,
        sessionStreamLogRepo,
        coderSessionRepo,
        eventBus,
        issueNumber: refreshedIssue.number,
        opencodeBinPath,
        model: resolveStageModel(Stage.Build, config),
      };

      const truncatedOutput = buildOutput.length > 8000 ? buildOutput.slice(-8000) : buildOutput;

      const prompt = [
        `## Build Fix Required`,
        '',
        `Issue #${refreshedIssue.number}: ${refreshedIssue.title}`,
        '',
        `The build failed after rebase. Fix all build errors and ensure the build passes.`,
        '',
        `## Build Error Output`,
        '',
        '```',
        truncatedOutput,
        '```',
        '',
        `## Instructions`,
        '',
        `1. Read the build error output above carefully`,
        `2. Fix each error in the relevant source files`,
        `3. Do NOT modify unrelated code — make minimal targeted fixes`,
        `4. Run \`npm run build\` in \`packages/cli\` to verify your fixes`,
        `5. If tests exist and are affected, run \`npm test\` to verify`,
        `6. Commit your fixes with a descriptive message`,
      ].join('\n');

      try {
        const session = await AgentSession.create(acpOptions);
        try {
          const result = await session.execute(prompt);
          if (!result.success) {
            return { success: false, error: result.error || 'Agent build fix session failed' };
          }
          return { success: true };
        } finally {
          await session.close().catch(() => {});
        }
      } catch (err) {
        return { success: false, error: err instanceof Error ? err.message : String(err) };
      }
    },
    onMergeSuccess: (entry) => {
      const issue = issueRepo.findById(entry.issueId);
      if (!issue) return;
      if (issue.stage === Stage.Done && issue.status === IssueStatus.Completed && issue.mergeState === MergeState.Merged) {
        log.info('onMergeSuccess: issue already completed, skipping', { issueNumber: entry.issueNumber });
        return;
      }
      if (issue.stage === Stage.Check) {
        log.info('onMergeSuccess: advancing issue to Done', { issueNumber: entry.issueNumber });
        issueRepo.updateStage(entry.issueId, Stage.Done);
        issueRepo.clearApprovalState(entry.issueId);
        issueRepo.updateStatus(entry.issueId, IssueStatus.Completed);
        issueRepo.setMergeState(entry.issueId, MergeState.Merged);
        issueRepo.updateBlockedReason(entry.issueId, null);
        eventBus.emit('agent_completed', {
          issueId: entry.issueId,
          projectId: entry.projectId,
          issueNumber: entry.issueNumber,
        });
      }
    },
  });

  mergeQueue.recoverFromDB();

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
  
  server.addRouter('/api/projects', createProjectRoutes(projectService));
  server.addRouter('/api/issues', createIssueRoutes(issueService, projectService, stateManager, worktreeManager, fileConfig, agentRunner, workflowLogRepo, sessionStreamLogRepo, stateManager.getCoderSessionRepo(), opencodeBinPath, mergeQueue, stateManager.getPipelineCheckpointRepo(), undefined, stateManager.getCheckSuiteRepo(), stateManager.getStageExecutionRepo()));
  server.addRouter('/api/propose', createProposeRoutes(issueService, projectService, stateManager, worktreeManager, fileConfig, agentRunner, opencodeBinPath));
  server.addRouter('/api/questions', createQuestionRoutes(stateManager.getQuestionRepo(), stateManager.getIssueRepo(), eventBus));
  server.addRouter('/api/labels', createLabelRoutes(projectService));
  server.addRouter('/api/config', createConfigRoutes(configService));
  server.addRouter('/api/providers', createProviderRoutes(eventBus, rateLimiter));
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

  eventBus.on('merge_completed', ({ issueId, issueNumber }) => {
    log.info('Merge completed, transitioning issue to done', { issueNumber });

    try {
      const issue = issueRepo.findById(issueId);
      if (!issue) return;
      if (issue.stage === Stage.Done && issue.status === IssueStatus.Completed && issue.mergeState === MergeState.Merged) {
        log.info('merge_completed: issue already completed, skipping', { issueNumber });
        return;
      }
      if (issue.stage === Stage.Check || issue.mergeState === MergeState.Merged) {
        issueRepo.updateStage(issueId, Stage.Done);
        issueRepo.updateStatus(issueId, IssueStatus.Completed);
        issueRepo.clearApprovalState(issueId);
        issueRepo.setMergeState(issueId, MergeState.Merged);
      }
    } catch (err) {
      log.error('Failed to transition issue to done after merge_completed', {
        issueNumber,
        error: err instanceof Error ? err.message : String(err),
      });
    }
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
