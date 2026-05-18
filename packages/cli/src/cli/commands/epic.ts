import { Command } from 'commander';
import chalk from 'chalk';
import { ApiResponse, Epic, EpicWithProgress, EpicDetail, EpicStatus, EpicPriority } from '../../types';
import { apiClient } from '../api-client';
import { requireServer } from '../server-check';

function formatEpicStatus(status: EpicStatus): string {
  const colors: Record<string, typeof chalk.green> = {
    active: chalk.green,
    done: chalk.blue,
    closed: chalk.gray,
  };
  const color = colors[status] || chalk.white;
  return color(status);
}

function formatEpicPriority(priority: EpicPriority): string {
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

function formatIssueStatus(status: string): string {
  const colors: Record<string, typeof chalk.green> = {
    active: chalk.green,
    paused: chalk.yellow,
    blocked: chalk.red,
    closed: chalk.gray,
    completed: chalk.green,
  };
  const color = colors[status] || chalk.white;
  return color(status);
}

function renderNextIssue(nextIssue: { id: string; number: number; title: string } | null, readyToMarkDone: boolean): void {
  if (nextIssue) {
    console.log(`  ${chalk.cyan('→')} ${chalk.bold(`#${nextIssue.number}`)} ${nextIssue.title}`);
  } else if (readyToMarkDone) {
    console.log(`  ${chalk.green('✓')} ${chalk.gray('Ready to mark done')}`);
  } else {
    console.log(`  ${chalk.gray('(no issues)')}`);
  }
}

function formatNextSummary(nextIssue: { id: string; number: number; title: string } | null, readyToMarkDone: boolean): string {
  if (nextIssue) {
    return `next: #${nextIssue.number} ${nextIssue.title.substring(0, 30)}`;
  }
  if (readyToMarkDone) {
    return `next: ${chalk.green('ready to mark done')}`;
  }
  return `next: ${chalk.gray('none')}`;
}

function renderEpicListRow(epic: EpicWithProgress): void {
  console.log(`  ${chalk.cyan(`#${epic.id}`)} ${chalk.bold(epic.title)}`);
  console.log(`    status: ${formatEpicStatus(epic.status)} · ` +
    `${epic.progress.deliveredCount}/${epic.progress.totalIssueCount} delivered · ` +
    formatNextSummary(epic.progress.nextIssue, epic.progress.readyToMarkDone));
  console.log();
}

export function setupEpicCommands(program: Command): void {
  const epic = program.command('epic').description('Manage Epics');

  epic.hook('preAction', async () => {
    await requireServer();
  });

  epic
    .command('create')
    .description('Create a new Epic')
    .requiredOption('-t, --title <title>', 'Epic title')
    .requiredOption('-d, --description <description>', 'Epic description')
    .requiredOption('-p, --priority <priority>', 'Epic priority (p0-p4)')
    .action(async (options) => {
      const validPriorities: EpicPriority[] = ['p0', 'p1', 'p2', 'p3', 'p4'];
      if (!validPriorities.includes(options.priority as EpicPriority)) {
        console.error(chalk.red(`Invalid priority: ${options.priority}. Must be one of: ${validPriorities.join(', ')}`));
        process.exit(1);
      }

      try {
        const response = await apiClient<ApiResponse<Epic>>(
          'POST',
          '/epics',
          { title: options.title, description: options.description, priority: options.priority }
        );

        if (response.success && response.data) {
          console.log(chalk.green(`✓ Created Epic #${response.data.id}`));
          console.log(chalk.gray(`  Title: ${response.data.title}`));
          console.log(chalk.gray(`  Priority: ${formatEpicPriority(response.data.priority)}`));
          console.log(chalk.gray(`  Status: ${formatEpicStatus(response.data.status)}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to create Epic: ${error}`));
        process.exit(1);
      }
    });

  epic
    .command('list')
    .description('List all Epics')
    .action(async () => {
      try {
        const response = await apiClient<ApiResponse<EpicWithProgress[]>>('GET', '/epics');

        if (!response.success) {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }

        if (response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow('No Epics found'));
            return;
          }

          const activeEpics = response.data.filter(e => e.status === EpicStatus.Active);
          const doneEpics = response.data.filter(e => e.status === EpicStatus.Done);
          const closedEpics = response.data.filter(e => e.status === EpicStatus.Closed);

          if (activeEpics.length > 0) {
            console.log(chalk.bold('\nActive\n'));
            for (const epic of activeEpics) {
              renderEpicListRow(epic);
            }
          }

          if (doneEpics.length > 0) {
            console.log(chalk.bold('Done\n'));
            for (const epic of doneEpics) {
              renderEpicListRow(epic);
            }
          }

          if (closedEpics.length > 0) {
            console.log(chalk.bold('Closed\n'));
            for (const epic of closedEpics) {
              renderEpicListRow(epic);
            }
          }
        }
      } catch (error) {
        console.error(chalk.red(`Failed to list Epics: ${error}`));
        process.exit(1);
      }
    });

  epic
    .command('show <id>')
    .description('Show Epic details')
    .action(async (id) => {
      try {
        const response = await apiClient<ApiResponse<EpicDetail>>('GET', `/epics/${id}`);

        if (!response.success) {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }

        if (response.data) {
          const epic = response.data;

          console.log(chalk.bold(`\nEpic #${epic.id}: ${epic.title}\n`));
          console.log(`  ${formatEpicStatus(epic.status)} · ${formatEpicPriority(epic.priority)}`);

          console.log(chalk.bold('\nProgress\n'));
          console.log(`  ${epic.progress.deliveredCount} / ${epic.progress.totalIssueCount} delivered`);

          console.log(chalk.bold('\nNext\n'));
          renderNextIssue(epic.progress.nextIssue, epic.progress.readyToMarkDone);

          if (epic.description) {
            console.log(chalk.bold('\nDescription\n'));
            console.log(`  ${epic.description.split('\n').join('\n  ')}`);
          }

          if (epic.linkedIssues && epic.linkedIssues.length > 0) {
            console.log(chalk.bold('\nIssues\n'));
            for (const issue of epic.linkedIssues) {
              const statusLabel = formatIssueStatus(issue.status);
              const numberStr = `[${statusLabel}]`.padEnd(12);
              console.log(`  ${numberStr} ${chalk.cyan(`#${issue.number}`)} ${issue.title}`);
            }
            console.log();
          } else {
            console.log(chalk.bold('\nIssues\n'));
            console.log(`  ${chalk.gray('(no issues linked)')}`);
            console.log();
          }
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show Epic: ${error}`));
        process.exit(1);
      }
    });

  epic
    .command('add-issue')
    .description('Add an issue to an Epic')
    .argument('<epic-id>', 'Epic ID')
    .argument('<issue-id>', 'Issue ID')
    .action(async (epicId, issueId) => {
      try {
        const response = await apiClient<ApiResponse<{ epicId: string; issueId: string }>>(
          'POST',
          `/epics/${epicId}/issues`,
          { issueId }
        );

        if (response.success) {
          console.log(chalk.green(`✓ Added issue #${issueId} to Epic #${epicId}`));
        } else {
          if (response.code === 'DUPLICATE_EPIC_MEMBERSHIP') {
            const details = response.details as { existingEpicId: string; existingEpicTitle: string } | undefined;
            console.error(chalk.red(`Error: Issue already belongs to Epic #${details?.existingEpicId || 'unknown'}: ${details?.existingEpicTitle || 'unknown'}`));
          } else {
            console.error(chalk.red(`Error: ${response.error}`));
          }
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to add issue to Epic: ${error}`));
        process.exit(1);
      }
    });

  epic
    .command('remove-issue')
    .description('Remove an issue from an Epic')
    .argument('<epic-id>', 'Epic ID')
    .argument('<issue-id>', 'Issue ID')
    .action(async (epicId, issueId) => {
      try {
        const response = await apiClient<ApiResponse<{ epicId: string; issueId: string }>>(
          'DELETE',
          `/epics/${epicId}/issues/${issueId}`
        );

        if (response.success) {
          console.log(chalk.green(`✓ Removed issue #${issueId} from Epic #${epicId}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to remove issue from Epic: ${error}`));
        process.exit(1);
      }
    });

  epic
    .command('done')
    .description('Mark an Epic as done')
    .argument('<id>', 'Epic ID')
    .action(async (id) => {
      try {
        const response = await apiClient<ApiResponse<Epic>>(
          'POST',
          `/epics/${id}/done`
        );

        if (response.success && response.data) {
          console.log(chalk.green(`✓ Marked Epic #${id} as done`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to mark Epic as done: ${error}`));
        process.exit(1);
      }
    });

  epic
    .command('close')
    .description('Close an Epic')
    .argument('<id>', 'Epic ID')
    .action(async (id) => {
      try {
        const response = await apiClient<ApiResponse<Epic>>(
          'POST',
          `/epics/${id}/close`
        );

        if (response.success && response.data) {
          console.log(chalk.green(`✓ Closed Epic #${id}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
          process.exit(1);
        }
      } catch (error) {
        console.error(chalk.red(`Failed to close Epic: ${error}`));
        process.exit(1);
      }
    });
}
