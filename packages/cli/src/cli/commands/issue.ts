import { Command } from 'commander';
import chalk from 'chalk';
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import { ApiResponse, Issue, Stage, VALID_PRIORITIES, normalizePriority } from '../../types';
import { slugify } from '../../utils/slugify';
import { apiClient } from '../api-client';
import { requireServer } from '../server-check';
import { classifyMergeDelivery, type MergeDeliveryStatus } from '../../workflow/issue-lifecycle';
import * as readline from 'readline';

export interface BodyIngestResult {
  body: string | undefined;
  error: string | null;
}

export async function ingestBody(bodyOpt: string | undefined, bodyFileOpt: string | undefined): Promise<BodyIngestResult> {
  if (bodyOpt && bodyFileOpt) {
    return { body: undefined, error: 'Cannot use both --body and --body-file. Use only one body source.' };
  }
  if (bodyOpt === '-') {
    return ingestStdin();
  }
  if (bodyOpt?.startsWith('@')) {
    const filePath = bodyOpt.slice(1);
    return ingestFile(filePath);
  }
  if (bodyFileOpt) {
    return ingestFile(bodyFileOpt);
  }
  return { body: bodyOpt, error: null };
}

function ingestFile(filePath: string): BodyIngestResult {
  try {
    const resolved = path.resolve(filePath);
    if (!fs.existsSync(resolved)) {
      return { body: undefined, error: `File not found: ${filePath}` };
    }
    const content = fs.readFileSync(resolved, 'utf-8');
    return { body: content, error: null };
  } catch (err: any) {
    return { body: undefined, error: `Failed to read file ${filePath}: ${err.message}` };
  }
}

function ingestStdin(): Promise<BodyIngestResult> {
  return new Promise((resolve) => {
    const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
    const lines: string[] = [];
    rl.on('line', (line) => lines.push(line));
    rl.on('close', () => resolve({ body: lines.join('\n'), error: null }));
    rl.on('error', (err) => resolve({ body: undefined, error: `Failed to read stdin: ${err.message}` }));
  });
}

function isStartable(issue: IssueWithPrerequisites): boolean {
  return issue.startEligibility?.startable === true;
}

function renderPrerequisites(prerequisites: IssuePrerequisiteSummary[] | undefined): void {
  if (!prerequisites || prerequisites.length === 0) return;

  console.log(chalk.gray('\n  Start Prerequisites:'));
  for (const prereq of prerequisites) {
    const deliveredColor = prereq.delivered ? chalk.green : chalk.yellow;
    const statusLabel = prereq.delivered ? 'delivered' : 'not delivered';
    console.log(`    ${deliveredColor(prereq.delivered ? '✓' : '○')} #${prereq.number}: ${prereq.title} (${statusLabel})`);
  }
}

function latestCurrentTruthChecks(checkResults: any[]): any[] {
  const latestByName = new Map<string, any>();
  for (const check of checkResults) {
    latestByName.set(check.name, check);
  }

  const rendered = new Set<string>();
  return checkResults.filter((check) => {
    if (latestByName.get(check.name) !== check) return false;
    if (rendered.has(check.name)) return false;
    rendered.add(check.name);
    return true;
  });
}

function formatStage(stage: string): string {
  const colors: Record<string, typeof chalk.green> = {
    draft: chalk.gray,
    plan: chalk.blue,
    build: chalk.cyan,
    check: chalk.yellow,
    done: chalk.green
  };
  
  const color = colors[stage] || chalk.white;
  return color(stage);
}

function formatStatus(status: string): string {
  const colors: Record<string, typeof chalk.green> = {
    active: chalk.green,
    paused: chalk.yellow,
    blocked: chalk.red
  };
  
  const color = colors[status] || chalk.white;
  return color(status);
}

function formatPriority(priority: string): string {
  const colors: Record<string, typeof chalk.green> = {
    p0: chalk.red.bold,
    p1: chalk.red,
    p2: chalk.yellow,
    p3: chalk.green,
    p4: chalk.gray,
  };
  const color = colors[priority] || chalk.white;
  return color(priority);
}

function formatArchivedAt(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleDateString();
  } catch {
    return iso;
  }
}

function formatLabels(labels: string[]): string {
  if (!labels || labels.length === 0) return '';
  return labels.map(l => chalk.blue(`[${l}]`)).join(' ');
}

export interface CoderSessionResponse {
  id: string;
  acpSessionId: string;
  status: string;
  createdAt: string;
  lastDataAt: string | null;
  probeSentAt: string | null;
  probeDeadlineAt: string | null;
  failureReason: string | null;
}

export interface IssuePrerequisiteSummary {
  issueId: string;
  number: number;
  title: string;
  delivered: boolean;
  stage: Stage;
  status: string;
  mergeState?: string | null;
}

export interface IssueStartEligibility {
  startable: boolean;
  reason: 'ready' | 'not-startable-lifecycle' | 'waiting-for-delivery';
  message?: string;
  waitingForDelivery: IssuePrerequisiteSummary[];
}

export interface IssueWithPrerequisites extends Issue {
  prerequisites?: IssuePrerequisiteSummary[];
  startEligibility?: IssueStartEligibility;
}

export interface IssueRecoveryProjection {
  workflowSummaryState?: string | null;
  latestAttemptState?: string | null;
  currentWorkItem?: {
    type: string;
    title: string;
  } | null;
  allowedActions?: string[];
}

function renderRecovery(recovery: IssueRecoveryProjection | null | undefined): void {
  if (!recovery) return;

  const attemptLabels: Record<string, string> = {
    running: chalk.green('Running'),
    completed: chalk.green('Completed'),
    failed: chalk.red('Failed'),
    interrupted: chalk.keyword('orange')('Interrupted'),
  };
  const summaryLabels: Record<string, string> = {
    running: chalk.green('Running'),
    'awaiting-approval': chalk.yellow('Awaiting Approval'),
    'waiting-for-recovery': chalk.yellow('Waiting for Recovery'),
    completed: chalk.green('Completed'),
  };

  const attemptState = recovery.latestAttemptState
    ? attemptLabels[recovery.latestAttemptState] ?? recovery.latestAttemptState
    : chalk.gray('N/A');
  const summaryState = recovery.workflowSummaryState
    ? summaryLabels[recovery.workflowSummaryState] ?? recovery.workflowSummaryState
    : chalk.gray('N/A');

  console.log(chalk.gray('\n  Recovery:'));
  console.log(`    Workflow: ${summaryState}`);
  console.log(`    Latest attempt: ${attemptState}`);

  if (recovery.currentWorkItem) {
    console.log(`    Current work: ${chalk.cyan(recovery.currentWorkItem.type)} — ${recovery.currentWorkItem.title}`);
  }

  if (recovery.allowedActions && recovery.allowedActions.length > 0) {
    const actionLabels: Record<string, string> = {
      wait: 'wait',
      stop: 'stop',
      retry: chalk.red('retry'),
      resume: chalk.yellow('resume'),
      rerun: 'rerun stage',
      inspect: 'inspect',
      approve: 'approve',
      reject: 'reject',
    };
    const actions = recovery.allowedActions.map((a: string) => actionLabels[a] ?? a).join(', ');
    console.log(`    Allowed actions: ${actions}`);
  }
}

async function renderIssueRecoveryFromApi(number: string | number): Promise<void> {
  const issueResponse = await apiClient<ApiResponse<any>>('GET', `/issues/${number}`);
  if (issueResponse.success && issueResponse.data?.recovery) {
    renderRecovery(issueResponse.data.recovery);
  }
}

export function formatSessionState(session: CoderSessionResponse | null): string {
  if (!session) return chalk.gray('No active session');

  if (session.status === 'running') return chalk.green('Running');
  if (session.status === 'probing') {
    let label = chalk.cyan('Checking session');
    if (session.probeSentAt && session.probeDeadlineAt) {
      const probeDeadline = new Date(session.probeDeadlineAt);
      const remaining = Math.max(0, Math.floor((probeDeadline.getTime() - Date.now()) / 1000));
      if (remaining > 0) {
        label = chalk.cyan(`Checking session (${remaining}s remaining)`);
      }
    }
    return label;
  }
  if (session.status === 'failed') {
    let label = chalk.red('Session failed');
    if (session.failureReason) {
      label = chalk.red(`Session failed: ${session.failureReason}`);
    }
    return label;
  }
  return chalk.gray('No active session');
}

function parseLabelFlags(flags: string[] | undefined): { add: string[]; remove: string[] } {
  const add: string[] = [];
  const remove: string[] = [];
  
  if (flags) {
    for (const flag of flags) {
      if (flag.startsWith('+')) {
        add.push(flag.slice(1));
      } else if (flag.startsWith('-')) {
        remove.push(flag.slice(1));
      } else {
        add.push(flag);
      }
    }
  }
  
  return { add, remove };
}

export function setupIssueCommands(program: Command): void {
  const issue = program.command('issue').description('Manage issues');

  issue.hook('preAction', async () => {
    await requireServer();
  });

  issue
    .command('create <title>')
    .description('Create a new issue')
    .option('-b, --body <body>', 'Issue body/description (use @file.md or - for file/stdin)')
    .option('--body-file <path>', 'Read body from a file')
    .option('-l, --label <label>', 'Add label (can be repeated)', (val, prev: string[]) => [...prev, val], [] as string[])
    .option('-p, --priority <level>', 'Set priority (p0-p4)')
    .option('--model <model>', 'Set coder model (provider/model)')
    .action(async (title, options) => {
      const normalizedPriority = normalizePriority(options.priority);
      if (options.priority !== undefined && normalizedPriority === null) {
        console.error(chalk.red(`Invalid priority: ${options.priority}. Must be one of: ${VALID_PRIORITIES.join(', ')}`));
        process.exit(1);
      }
      const bodyResult = await ingestBody(options.body, options.bodyFile);
      if (bodyResult.error) {
        console.error(chalk.red(`Error: ${bodyResult.error}`));
        process.exit(1);
      }
      try {
        const response = await apiClient<ApiResponse<IssueWithPrerequisites>>(
          'POST',
          '/issues',
          { title, body: bodyResult.body, labels: options.label, priority: normalizedPriority, model: options.model }
        );

        if (response.success && response.data) {
          const issue = response.data;
          console.log(chalk.green(`✓ Created issue #${issue.number}: ${issue.title}`));
          console.log(chalk.gray(`  Priority: ${formatPriority(issue.priority)}`));
          if (isStartable(issue)) {
            console.log(chalk.cyan(`  Tip: Run '${chalk.bold(`mo issue start ${issue.number}`)}' to begin processing`));
          }
          if (issue.startEligibility && !issue.startEligibility.startable && issue.startEligibility.message) {
            console.log(chalk.yellow(`  Waiting: ${issue.startEligibility.message}`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to create issue: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('list')
    .description('List issues')
    .option('-s, --status <stages>', 'Filter by stage(s) - use comma for multiple (e.g. -s build,check) or alias (e.g. -s active)')
    .option('-l, --label <label>', 'Filter by label')
    .option('-p, --priority <level>', 'Filter by priority (p0-p4)')
    .option('--archived', 'Show only archived issues')
    .option('--all', 'Show all issues including archived')
    .option('--attention', 'Show only issues needing user action or decision')
    .action(async (options) => {
      if (options.priority !== undefined) {
        const normalizedPriority = normalizePriority(options.priority);
        if (normalizedPriority === null) {
          console.error(chalk.red(`Invalid priority: ${options.priority}. Must be one of: ${VALID_PRIORITIES.join(', ')}`));
          process.exit(1);
        }
      }
      try {
        let path = '/issues';
        const params: string[] = [];
        if (options.status) {
          params.push(`stage=${options.status}`);
        }
        if (options.label) {
          params.push(`label=${options.label}`);
        }
        if (options.priority) {
          params.push(`priority=${normalizePriority(options.priority)}`);
        }
        if (options.archived) {
          params.push('archived=true');
        } else if (options.all) {
          params.push('all=true');
        }
        if (options.attention) {
          params.push('attention=true');
        }
        if (params.length > 0) {
          path += `?${params.join('&')}`;
        }

        const response = await apiClient<ApiResponse<Issue[]>>('GET', path);

        if (!response.success) {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }

        if (response.data) {
          if (response.data.length === 0) {
            if (options.attention) {
              console.log(chalk.yellow('No issues requiring attention'));
            } else {
              console.log(chalk.yellow(options.archived ? 'No archived issues' : 'No issues found'));
            }
            return;
          }

          const header = options.archived ? 'Archived Issues:' : options.attention ? 'Attention Issues:' : 'Issues:';
          console.log(chalk.bold(`\n${header}\n`));

          if (options.archived || options.all) {
            console.log('  ID                 Priority  Stage                     Status    Labels              Title                                       Archived');
            console.log('  ' + '─'.repeat(130));
          } else {
            console.log('  ID                 Priority  Stage                     Status    Labels              Title');
            console.log('  ' + '─'.repeat(105));
          }

          response.data.forEach((issue: any) => {
            const id = chalk.cyan(`${issue.projectName || 'unknown'}#${issue.number}`.padEnd(18));
            const priority = formatPriority(issue.priority || 'p2').padEnd(9);
            const stage = formatStage(issue.stage).padEnd(25);
            const status = formatStatus(issue.status).padEnd(9);
            const labels = formatLabels(issue.labels).padEnd(20);
            const titlePart = issue.title.substring(0, 40);

            const mergeStatus = classifyMergeDelivery(issue);
            const mergeWarning = mergeStatus === 'done-not-merged' ? chalk.red.bold(' [UNMERGED]') : '';

            const waitingWarning = issue.startEligibility?.waitingForDelivery?.length
              ? chalk.yellow.bold(` [Waiting for #${issue.startEligibility.waitingForDelivery[0].number}]`)
              : '';

            if (options.archived || options.all) {
              const archivedCol = issue.archivedAt
                ? chalk.gray(formatArchivedAt(issue.archivedAt)).padEnd(24)
                : '';
              const suffix = issue.archivedAt && !options.archived ? chalk.gray(' (archived)') : '';
              console.log(`  ${id} ${priority} ${stage} ${status} ${labels} ${titlePart.padEnd(42)}${archivedCol}${suffix}${mergeWarning}${waitingWarning}`);
            } else {
              console.log(`  ${id} ${priority} ${stage} ${status} ${labels} ${titlePart}${mergeWarning}${waitingWarning}`);
            }
          });
          console.log();
        }
      } catch (error) {
        console.error(chalk.red(`Failed to list issues: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('show <number>')
    .description('Show issue details')
    .option('--compact', 'Print one-line summary (skips sessions, checks, comments, body)')
    .action(async (number, options) => {
      try {
        const response = await apiClient<ApiResponse<any>>(
          'GET',
          `/issues/${number}`
        );

        if (response.success && response.data) {
          const issue = response.data;
          const displayId = `${issue.projectName || 'unknown'}#${issue.number}`;

          if (options.compact) {
            const stage = issue.stage || 'unknown';
            const status = issue.status || 'unknown';
            const priority = issue.priority || 'p2';
            const title = issue.title || '';
            console.log(`#${issue.number} ${stage} ${status} ${priority} "${title}"`);
            return;
          }

          console.log(chalk.bold(`\nIssue ${displayId}: ${issue.title}\n`));
          console.log(`  Priority: ${formatPriority(issue.priority || 'p2')}`);
          console.log(`  Stage: ${formatStage(issue.stage)}`);
          console.log(`  Status: ${formatStatus(issue.status)}`);

          const mergeStatus = classifyMergeDelivery(issue);
          const mergeStatusColors: Record<MergeDeliveryStatus, typeof chalk.green> = {
            merged: chalk.green,
            queued: chalk.blue,
            rebasing: chalk.blue,
            merging: chalk.blue,
            resolving: chalk.yellow,
            conflict: chalk.red,
            'build-failed': chalk.red,
            blocked: chalk.red,
            'not-ready': chalk.gray,
            'not-merged': chalk.yellow,
            unknown: chalk.gray,
            'done-not-merged': chalk.red,
            integrating: chalk.cyan,
          };
          const mergeColor = mergeStatusColors[mergeStatus] || chalk.white;
          const mergeStatusLabels: Record<MergeDeliveryStatus, string> = {
            merged: 'Merged',
            queued: 'Queued for merge',
            rebasing: 'Rebasing',
            merging: 'Merging',
            resolving: 'Resolving conflicts',
            conflict: 'Merge conflict',
            'build-failed': 'Build failed after merge',
            blocked: 'Blocked',
            'not-ready': 'Not ready for merge',
            'not-merged': 'Not merged',
            unknown: 'Unknown',
            'done-not-merged': 'DONE BUT NOT MERGED',
            integrating: 'Integrating',
          };
          console.log(`  Merge: ${mergeColor(mergeStatusLabels[mergeStatus])}`);

          if (issue.baseBranch) {
            const sourceBranch = `mo/issue-${issue.number}`;
            const targetBranch = issue.baseBranch;
            console.log(`  Branch: ${chalk.cyan(sourceBranch)} → ${chalk.cyan(targetBranch)}`);
          }

          if (mergeStatus === 'done-not-merged') {
            console.log(chalk.red.bold('  WARNING: This issue is marked done/completed but has not been merged!'));
          }

          if (issue.archivedAt) {
            console.log(`  Archived: ${chalk.gray(new Date(issue.archivedAt).toLocaleString())}`);
          }

          const sessionsResponse = await apiClient<ApiResponse<CoderSessionResponse[]>>(
            'GET',
            `/issues/${number}/coder-sessions`
          );
          let currentSession: CoderSessionResponse | null = null;
          if (sessionsResponse.success && sessionsResponse.data) {
            const activeStatuses = ['running', 'probing', 'failed'];
            currentSession = sessionsResponse.data
              .filter(s => activeStatuses.includes(s.status))
              .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime())[0] || null;
          }
          console.log(`  Session: ${formatSessionState(currentSession)}`);

          renderRecovery(issue.recovery);

          const executionsResponse = await apiClient<ApiResponse<any[]>>(
            'GET',
            `/issues/${number}/executions`
          );

          if (executionsResponse.success && executionsResponse.data && executionsResponse.data.length > 0) {
            console.log(chalk.gray('\n  Stage Checks:'));

            const latestByStage = new Map<string, any>();
            for (const execution of executionsResponse.data) {
              latestByStage.set(execution.stage, execution);
            }

            for (const execution of latestByStage.values()) {
              if (execution.checkResults && execution.checkResults.length > 0) {
                console.log(chalk.gray(`    [${execution.stage}]`));
                for (const check of latestCurrentTruthChecks(execution.checkResults)) {
                  const isHealthGate = check.name.startsWith('health:') || (check.output && (check.output as any).kind === 'health-gate');
                  const isInternalName = check.name === 'ai-review' || check.name === 'merge-readiness' || check.name === 'integration-health-gate-preview';
                  if ((isHealthGate || isInternalName) && check.name !== 'health:check') continue;
                  const checkIcon = check.status === 'pass' ? chalk.green('✓') : check.status === 'fail' ? chalk.red('✗') : check.status === 'error' ? chalk.red('✗') : chalk.gray('○');
                  const displayName = check.name;
                  console.log(`      ${checkIcon} ${displayName}`);
                  if (check.status === 'fail' || check.status === 'error') {
                    if (check.message) {
                      console.log(chalk.red(`        Error: ${check.message}`));
                    }
                    if (isHealthGate && check.output) {
                      const output = check.output as any;
                      if (output.command) console.log(chalk.gray(`        Command: ${output.command}`));
                      if (output.duration) console.log(chalk.gray(`        Duration: ${output.duration}ms`));
                      if (output.summary) console.log(chalk.red(`        Summary: ${output.summary}`));
                      if (output.logExcerpt) {
                        const excerptLines = output.logExcerpt.split('\n').slice(0, 5);
                        if (excerptLines.length > 0) {
                          console.log(chalk.gray(`        Log excerpt:`));
                          for (const line of excerptLines) {
                            console.log(chalk.gray(`          ${line}`));
                          }
                        }
                      }
                    }
                  }
                  if (check.duration) {
                    console.log(chalk.gray(`        Duration: ${check.duration}ms`));
                  }
                }
              }
            }
          }

          if (issue.approvalState) {
            const statusColors: Record<string, typeof chalk.green> = {
              awaiting: chalk.yellow,
              approved: chalk.green,
              rejected: chalk.red,
              error: chalk.red,
            };
            const as = issue.approvalState;
            const color = statusColors[as.status] || chalk.white;

            const isStaleApproval = issue.staleEvidence?.approval === true;
            if (isStaleApproval) {
              console.log(`  Approval: ${chalk.red('STALE')} (stage: ${as.stage})`);
              console.log(chalk.yellow('  ⚠ Approval evidence is stale — base has advanced. Rebase or rerun checks before approving.'));
            } else {
              console.log(`  Approval: ${color(as.status)} (stage: ${as.stage})`);
            }
            if (as.status === 'error' && as.output?.error) {
              console.log(`  ${chalk.red(`Error: ${as.output.error}`)}`);
            } else if (as.output && !isStaleApproval) {
              const notes = typeof as.output === 'string' ? as.output : JSON.stringify(as.output, null, 2);
              console.log(`  Self-review notes:`);
              console.log(`  ${chalk.gray(notes.split('\n').join('\n  '))}`);
            }
          }
          
          renderPrerequisites(issue.prerequisites);

          if (issue.startEligibility && !issue.startEligibility.startable) {
            console.log(chalk.yellow(`  Waiting: ${issue.startEligibility.message}`));
          }

          if (issue.drifted) {
            console.log(chalk.yellow('\n  ⚠ Base Drift Detected'));
            const decision = issue.decision;
            const decisionLabels: Record<string, string> = {
              'skip': 'Aligned with base',
              'suggest': 'Rebase recommended',
              'enqueue': 'Rebase queued',
              'defer': 'Rebase deferred',
              'needs-attention': 'Needs attention',
            };
            if (decision && decisionLabels[decision]) {
              console.log(`    Status: ${chalk.yellow(decisionLabels[decision])}`);
            }
            if (issue.deferReason) {
              const deferReasonLabels: Record<string, string> = {
                'agent-running': 'Agent is currently running',
                'task-running': 'A task is currently executing',
                'waiting-for-task-boundary': 'Waiting for task boundary',
                'rebase-already-pending': 'Rebase is already pending',
              };
              const reasonLabel = deferReasonLabels[issue.deferReason] || issue.deferReason;
              console.log(`    Defer reason: ${chalk.gray(reasonLabel)}`);
            }
            if (issue.conflicts && issue.conflicts.length > 0) {
              console.log(chalk.red(`    Conflicts: ${issue.conflicts.join(', ')}`));
            }
            if (issue.nextAction) {
              console.log(`    ${chalk.cyan('→')} ${issue.nextAction}`);
            }
          }

          if (issue.labels && issue.labels.length > 0) {
            console.log(`  Labels: ${formatLabels(issue.labels)}`);
          }

          if (issue.body) {
            console.log(`\n  ${chalk.gray('Body:')}`);
            console.log(`  ${issue.body.split('\n').join('\n  ')}`);
          }

          if (issue.comments && issue.comments.length > 0) {
            console.log(`\n  ${chalk.gray('Comments:')} (${issue.comments.length})`);
            issue.comments.forEach((comment: any) => {
              const shortId = comment.id.substring(0, 8);
              console.log(`    [${chalk.cyan(shortId)}] ${chalk.gray(new Date(comment.createdAt).toLocaleString())}`);
              console.log(`    ${comment.body.split('\n').join('\n    ')}`);
              console.log();
            });
          }

          console.log();
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show issue: ${error}`));
      }
    });

  issue
    .command('status <number>')
    .description('Show issue status and recovery guidance')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<any>>(
          'GET',
          `/issues/${number}`
        );

        if (response.success && response.data) {
          const issue = response.data;
          const displayId = `${issue.projectName || 'unknown'}#${issue.number}`;
          console.log(chalk.bold(`\nIssue ${displayId}: ${issue.title}\n`));
          console.log(`  Stage: ${formatStage(issue.stage || 'unknown')}`);
          console.log(`  Status: ${formatStatus(issue.status || 'unknown')}`);
          renderRecovery(issue.recovery);
          console.log();
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show issue status: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('add-prerequisite <number> <prerequisite-number>')
    .description('Declare an issue-level start prerequisite')
    .action(async (number, prerequisiteNumber) => {
      try {
        const issueNumber = parseInt(number, 10);
        const requiredNumber = parseInt(prerequisiteNumber, 10);

        if (Number.isNaN(issueNumber) || Number.isNaN(requiredNumber)) {
          console.error(chalk.red('Error: issue number and prerequisite number must both be integers'));
          process.exit(1);
        }

        const response = await apiClient<ApiResponse<{ issue: IssueWithPrerequisites; message: string; reason?: string }>>(
          'POST',
          `/issues/${issueNumber}/prerequisites`,
          { prerequisiteNumber: requiredNumber }
        );

        if (response.success && response.data?.issue) {
          console.log(chalk.green(`✓ ${response.data.message}`));
          renderPrerequisites(response.data.issue.prerequisites);
          if (response.data.issue.startEligibility && !response.data.issue.startEligibility.startable && response.data.issue.startEligibility.message) {
            console.log(chalk.yellow(`  Waiting: ${response.data.issue.startEligibility.message}`));
          }
          return;
        }

        const reason = (response.data as { reason?: string } | undefined)?.reason;
        if (reason === 'circular-prerequisite') {
          console.error(chalk.red(`Error: ${response.error}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
        process.exit(1);
      } catch (error) {
        console.error(chalk.red(`Failed to add prerequisite: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('update <number>')
    .description('Update an issue')
    .option('--title <title>', 'New title')
    .option('--body <body>', 'New body (use @file.md or - for file/stdin)')
    .option('-l, --label <label>', 'Add (+label) or remove (-label) label', (val, prev: string[]) => [...prev, val], [] as string[])
    .option('-p, --priority <level>', 'Set priority (p0-p4)')
    .action(async (number, options) => {
      try {
        const normalizedPriority = normalizePriority(options.priority);
        if (options.priority !== undefined && normalizedPriority === null) {
          console.error(chalk.red(`Invalid priority: ${options.priority}. Must be one of: ${VALID_PRIORITIES.join(', ')}`));
          process.exit(1);
        }
        const bodyResult = await ingestBody(options.body, undefined);
        if (bodyResult.error) {
          console.error(chalk.red(`Error: ${bodyResult.error}`));
          process.exit(1);
        }
        const { add, remove } = parseLabelFlags(options.label);

        const payload: {
          title?: string;
          body?: string;
          priority?: typeof normalizedPriority;
          addLabels?: string[];
          removeLabels?: string[];
        } = {
          title: options.title,
          body: bodyResult.body,
          addLabels: add.length > 0 ? add : undefined,
          removeLabels: remove.length > 0 ? remove : undefined,
        };
        if (options.priority !== undefined) {
          payload.priority = normalizedPriority;
        }

        const response = await apiClient<ApiResponse<Issue>>(
          'PATCH',
          `/issues/${number}`,
          payload
        );

        if (response.success && response.data) {
          console.log(chalk.green(`✓ Updated issue #${response.data.number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to update issue: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('close <number>')
    .description('Close an issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/close`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Closed issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to close issue: ${error}`));
      }
    });

  issue
    .command('archive')
    .description('Archive an issue (or all completed issues)')
    .option('--all-completed', 'Archive all completed issues')
    .option('--no-cleanup', 'Skip worktree and openspec cleanup')
    .argument('[number]', 'Issue number to archive')
    .action(async (number, options) => {
      try {
        if (options.allCompleted) {
          if (options.noCleanup) {
            console.error(chalk.red('Error: --no-cleanup is not supported for batch archive. Use "mo issue archive <number> --no-cleanup" for single-issue archive without cleanup.'));
            return;
          }

          const response = await apiClient<ApiResponse>(
            'POST',
            '/issues/archive-completed'
          );

          if (response.success && response.data) {
            const { archived = 0, skipped = 0, skippedNumbers = [], message } = response.data as any;
            if (archived === 0 && skipped === 0) {
              console.log(chalk.yellow('No completed issues to archive'));
            } else {
              console.log(chalk.green(`✓ Archived ${archived} completed issue(s)`));
              if (skipped > 0) {
                console.log(chalk.yellow(`  Skipped ${skipped} false-done issue(s) (not merged): #${skippedNumbers.join(', #')}`));
              }
            }
            if (message) {
              console.log(chalk.gray(`  ${message}`));
            }
          } else {
            console.error(chalk.red(`Error: ${response.error}`));
          }
          return;
        }

        if (!number) {
          console.error(chalk.red('Error: provide an issue number or --all-completed'));
          return;
        }

        const body: { cleanup: boolean } = { cleanup: options.cleanup !== false };
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/archive`,
          body
        );

        if (response.success) {
          console.log(chalk.green(`✓ Archived issue #${number}`));
          if (response.data?.warning) {
            console.log(chalk.yellow(`  ${response.data.warning}`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to archive issue: ${error}`));
      }
    });

  issue
    .command('unarchive <number>')
    .description('Unarchive an issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/unarchive`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Unarchived issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to unarchive issue: ${error}`));
      }
    });

  issue
    .command('reopen <number>')
    .description('Reopen a closed issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/reopen`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Reopened issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to reopen issue: ${error}`));
      }
    });

  issue
    .command('comment <number> <text>')
    .description('Add a comment to an issue')
    .action(async (number, text) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/comments`,
          { body: text }
        );

        if (response.success) {
          console.log(chalk.green(`✓ Comment added to issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to add comment: ${error}`));
      }
    });

  issue
    .command('delete-comment <number> <comment-id>')
    .description('Delete a comment from an issue')
    .action(async (number, commentId) => {
      try {
        const response = await apiClient<ApiResponse>(
          'DELETE',
          `/issues/${number}/comments/${commentId}`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Deleted comment ${commentId} from issue #${number}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to delete comment: ${error}`));
      }
    });

  issue
    .command('start <number>')
    .description('Start processing an issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<{ startEligibility?: IssueStartEligibility; taskId?: string }>>(
          'POST',
          `/issues/${number}/start`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Started processing issue #${number}`));
          if (response.data?.taskId) {
            console.log(chalk.gray(`  Task ID: ${response.data.taskId}`));
          }
          await renderIssueRecoveryFromApi(number);
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          if (response.data?.startEligibility) {
            const se = response.data.startEligibility;
            if (se.waitingForDelivery.length > 0) {
              console.error(chalk.yellow(`  Waiting for: ${se.waitingForDelivery.map(p => `#${p.number}`).join(', ')}`));
            }
          }
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to start issue: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('approve <number>')
    .description('Approve an issue awaiting approval')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<any>>(
          'POST',
          `/issues/${number}/approve`
        );

        if (response.success) {
          const message = response.data?.message;
          if (message) {
            console.log(chalk.green(`✓ ${message}`));
          } else {
            console.log(chalk.green(`✓ Issue #${number} approved`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to approve issue: ${error}`));
      }
    });

  issue
    .command('reject <number>')
    .description('Reject an issue awaiting approval')
    .option('-m, --message <message>', 'Rejection reason')
    .action(async (number, options) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/reject`,
          { message: options.message }
        );
        
        if (response.success) {
          const message = response.data?.message;
          if (message) {
            console.log(chalk.yellow(`✓ ${message}`));
          } else {
            console.log(chalk.yellow(`✓ Issue #${number} rejected`));
          }
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to reject issue: ${error}`));
      }
    });

  issue
    .command('resume <number>')
    .description('Resume a paused or interrupted issue')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse>(
          'POST',
          `/issues/${number}/resume`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Resumed issue #${number}`));
          await renderIssueRecoveryFromApi(number);
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to resume issue: ${error}`));
      }
    });

  issue
    .command('retry <number>')
    .description('Retry the failed current work item')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<any>>(
          'POST',
          `/issues/${number}/retry`
        );

        if (response.success) {
          const message = response.data?.message;
          console.log(chalk.green(`✓ ${message || `Issue #${number} retry started`}`));
          await renderIssueRecoveryFromApi(number);
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to retry issue: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('rerun <number>')
    .description('Rerun the current stage')
    .action(async (number) => {
      try {
        const response = await apiClient<ApiResponse<any>>(
          'POST',
          `/issues/${number}/rerun`
        );

        if (response.success) {
          const message = response.data?.message;
          console.log(chalk.green(`✓ ${message || `Issue #${number} rerun started`}`));
          await renderIssueRecoveryFromApi(number);
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to rerun issue: ${error}`));
        process.exit(1);
      }
    });

  issue
    .command('diff <number>')
    .description('Show diff between issue branch and main')
    .option('--stat', 'Show file-level change statistics without patch content')
    .action(async (number, options) => {
      try {
        const diffResponse = await apiClient<ApiResponse<any>>(
          'GET',
          `/issues/${number}/diff`
        );

        if (!diffResponse.success) {
          console.error(chalk.red(`Error: ${diffResponse.error}`));
          process.exit(1);
        }

        const diffData = diffResponse.data;

        if (!diffData.available) {
          const reasonMessages: Record<string, string> = {
            not_started: 'Issue has not started yet (no worktree or branch)',
            worktree_removed: 'Worktree has been removed',
            branch_missing: 'Branch not found',
            git_error: 'Git error occurred while loading diff',
          };
          const reasonLabel = reasonMessages[diffData.reason] || diffData.reason;
          console.error(chalk.red(`Diff unavailable: ${reasonLabel}`));
          if (diffData.message) {
            console.error(chalk.gray(`  ${diffData.message}`));
          }
          process.exit(1);
        }

        if (options.stat) {
          const { summary, files } = diffData;
          if (summary.filesChanged === 0) {
            console.log(chalk.yellow('No changes in diff'));
            return;
          }
          console.log(chalk.bold(`\nDiff statistics for #${number} (${summary.filesChanged} file(s)):\n`));
          console.log(`  ${chalk.green('+'+summary.additions)} additions  ${chalk.red('-'+summary.deletions)} deletions\n`);
          console.log('  File');
          console.log('  ' + '─'.repeat(60));
          for (const file of files) {
            const binaryMarker = file.isBinary ? ' (binary)' : '';
            const addStr = file.additions > 0 ? chalk.green(`+${file.additions}`) : '+0';
            const delStr = file.deletions > 0 ? chalk.red(`-${file.deletions}`) : '-0';
            console.log(`  ${addStr} ${delStr}  ${file.file}${binaryMarker}`);
          }
          console.log();
        } else {
          let hasChanges = false;
          for (const file of diffData.files) {
            if (file.diff) {
              hasChanges = true;
              process.stdout.write(file.diff);
              if (!file.diff.endsWith('\n')) {
                process.stdout.write('\n');
              }
            }
          }
          if (!hasChanges && diffData.summary.filesChanged === 0) {
            console.log(chalk.yellow('No changes in diff'));
          }
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show diff: ${error}`));
        process.exit(1);
      }
    });

  const PIPELINE_EVENT_TYPES = 'build_started,build_completed,build_failed,task_started,task_completed,task_failed';

  function formatPipelineEventType(eventType: string): string {
    const labels: Record<string, string> = {
      build_started: 'BUILD START',
      build_completed: 'BUILD DONE',
      build_failed: 'BUILD FAIL',
      task_started: 'TASK START',
      task_completed: 'TASK DONE',
      task_failed: 'TASK FAIL',
    };
    return labels[eventType] || eventType;
  }

  function formatPipelineEventTimestamp(createdAt: string): string {
    try {
      const d = new Date(createdAt);
      return d.toLocaleString();
    } catch {
      return createdAt;
    }
  }

  function summarizePipelineEvent(event: any): string {
    const data = event.data || {};
    switch (event.eventType) {
      case 'build_started':
        return `Build stage started${data.changeId ? ` (change: ${data.changeId})` : ''}`;
      case 'build_completed':
        return `Build stage completed${data.completed != null && data.total != null ? ` (${data.completed}/${data.total} tasks)` : ''}`;
      case 'build_failed':
        return `Build stage failed${data.error ? `: ${data.error}` : ''}`;
      case 'task_started':
        return `Task started: ${data.taskId || data.title || 'unknown'}${data.title ? ` - ${data.title}` : ''}`;
      case 'task_completed':
        return `Task completed: ${data.taskId || 'unknown'}${data.title ? ` - ${data.title}` : ''}`;
      case 'task_failed':
        return `Task failed: ${data.taskId || 'unknown'}${data.error ? ` - ${data.error}` : ''}`;
      default:
        return JSON.stringify(data);
    }
  }

  issue
    .command('logs <number>')
    .description('Show agent logs for an issue')
    .option('-f, --follow', 'Follow log output in real time')
    .action(async (number, options) => {
      try {
        const detailResponse = await apiClient<ApiResponse<any>>(
          'GET',
          `/issues/${number}`
        );

        if (!detailResponse.success || !detailResponse.data) {
          console.error(chalk.red(`Error: ${detailResponse.error}`));
          return;
        }

        const issue = detailResponse.data;

        let hasPipelineEvents = false;
        try {
          const logsResponse = await apiClient<ApiResponse<any[]>>(
            'GET',
            `/issues/${number}/logs?eventType=${PIPELINE_EVENT_TYPES}`
          );

          if (logsResponse.success && logsResponse.data && logsResponse.data.length > 0) {
            hasPipelineEvents = true;
            console.log(chalk.bold('\nPipeline Events:\n'));
            console.log(`  ${'Timestamp'.padEnd(22)} ${'Event'.padEnd(14)} Summary`);
            console.log('  ' + '─'.repeat(80));
            for (const event of logsResponse.data) {
              const ts = formatPipelineEventTimestamp(event.createdAt).padEnd(22);
              const typeLabel = formatPipelineEventType(event.eventType);
              const typeColor: Record<string, typeof chalk.green> = {
                'BUILD START': chalk.blue,
                'BUILD DONE': chalk.green,
                'BUILD FAIL': chalk.red,
                'TASK START': chalk.cyan,
                'TASK DONE': chalk.green,
                'TASK FAIL': chalk.red,
              };
              const colored = (typeColor[typeLabel] || chalk.white)(typeLabel).padEnd(22);
              const summary = summarizePipelineEvent(event);
              console.log(`  ${ts} ${colored} ${summary}`);
            }
            console.log();
          }
        } catch {
          // pipeline events unavailable, continue to file logs
        }

        if (options.follow) {
          if (hasPipelineEvents) {
            console.log(chalk.yellow('Note: --follow shows file logs only. Pipeline events are above.\n'));
          }
        }

        const projectName = issue.projectName;
        if (!projectName || projectName === 'unknown') {
          if (!hasPipelineEvents) {
            console.error(chalk.red('Error: No project name found'));
          }
          return;
        }

        const slug = slugify(projectName);
        const home = process.env.HOME || '';
        const logDir = path.join(home, '.mohist', 'projects', slug, 'logs', `issue-${number}`);

        if (!fs.existsSync(logDir)) {
          if (!hasPipelineEvents) {
            console.error(chalk.red(`No logs found for issue #${number}`));
          }
          return;
        }

        const logFiles = fs.readdirSync(logDir)
          .filter(f => f.endsWith('.log'))
          .sort();

        if (logFiles.length === 0) {
          if (!hasPipelineEvents) {
            console.error(chalk.red(`No logs found for issue #${number}`));
          }
          return;
        }

        if (options.follow) {
          const logFile = logFiles[logFiles.length - 1];
          const logPath = path.join(logDir, logFile);

          const tail = spawn('tail', ['-f', '-n', '50', logPath], {
            stdio: 'inherit'
          });

          tail.on('error', (err) => {
            console.error(chalk.red(`Failed to tail logs: ${err.message}`));
          });

          process.on('SIGINT', () => {
            tail.kill();
            process.exit(0);
          });
        } else {
          console.log(chalk.bold('Agent Session Logs:\n'));
          for (const logFile of logFiles) {
            const logPath = path.join(logDir, logFile);
            const content = fs.readFileSync(logPath, 'utf-8');
            const lines = content.split('\n');
            const lastLines = lines.slice(-50);
            console.log(chalk.bold(`--- ${logFile} ---`));
            console.log(lastLines.join('\n'));
            console.log();
          }
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show logs: ${error}`));
      }
    });
}

export function setupLabelCommands(program: Command): void {
  const label = program.command('label').description('Manage labels');

  label.hook('preAction', async () => {
    await requireServer();
  });

  label
    .command('list')
    .description('List all labels used in the current project')
    .action(async () => {
      try {
        const response = await apiClient<ApiResponse<string[]>>(
          'GET',
          '/labels'
        );
        
        if (response.success && response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow('No labels found'));
            return;
          }
          
          console.log(chalk.bold('\nLabels:\n'));
          response.data.forEach((labelName) => {
            console.log(`  ${chalk.blue(`[${labelName}]`)}`);
          });
          console.log();
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to list labels: ${error}`));
      }
    });
}
