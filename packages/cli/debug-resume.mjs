import { DatabaseManager } from './dist/db/database.js';
import { initializeDatabase } from './dist/db/migrations.js';
import { ProjectRepo } from './dist/db/project-repo.js';
import { IssueRepo } from './dist/db/issue-repo.js';
import { IssueService } from './dist/services/issue-service.js';
import { EventBus } from './dist/services/event-bus.js';
import { AgentRunnerService } from './dist/services/agent-runner-service.js';
import { Stage, IssueStatus } from './dist/types.js';
import { StateManager } from './dist/server/state-manager.js';
import { CommentRepo } from './dist/db/comment-repo.js';
import { LabelRepo } from './dist/db/label-repo.js';
import { ProjectService } from './dist/services/project-service.js';
import { IssueTaskQueueRepo } from './dist/services/index.js';

const db = new DatabaseManager({ inMemory: true });
initializeDatabase(db);
const stateManager = new StateManager(db);
const projectRepo = stateManager.getProjectRepo();
const issueRepo = stateManager.getIssueRepo();
const configRepo = stateManager.getConfigRepo();
const commentRepo = stateManager.getCommentRepo();
const labelRepo = stateManager.getLabelRepo();
const projectService = new ProjectService(projectRepo, configRepo, issueRepo, labelRepo);
const issueService = new IssueService(issueRepo, commentRepo);
const eventBus = new EventBus();
const issueTaskQueueRepo = stateManager.getIssueTaskQueueRepo();
const agentRunner = new AgentRunnerService(eventBus, undefined, issueRepo, 8, undefined, undefined, projectRepo, undefined, issueTaskQueueRepo);

const project = await projectService.create({ name: 'TestProject', path: '/test' });
projectService.setCurrent(project);
const issue = issueService.create({ projectId: project.id, title: 'Paused Issue' });
const issueId = issue.id;
issueRepo.updateStatus(issueId, IssueStatus.Paused);
issueRepo.updateStage(issueId, Stage.Build);

console.log('Initial status:', issueRepo.findById(issueId).status);

// Simulate the resume handler steps
const resumedIssue = issueService.resume(project.id, issue.number);
console.log('After resume():', resumedIssue?.status);

// Now call recoverSingleIssueById like the API does
agentRunner.recoverSingleIssueById(issueId);
console.log('After recoverSingleIssueById:', issueRepo.findById(issueId).status);
