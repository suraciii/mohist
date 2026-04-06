import { Command } from 'commander';
import chalk from 'chalk';
import { connectSSE } from '../sse-client';
import type { ClientRequest } from 'http';
import { formatEvent } from '../event-formatter';
import { apiClient, API_BASE } from '../api-client';
import { requireServer } from '../index';

const SSE_BASE = API_BASE.replace('/api', '');

async function resolveProjectId(name: string): Promise<string | null> {
  const response = await apiClient<any>('GET', '/projects');
  if (!response.success || !response.data) return null;
  const project = response.data.find((p: any) => p.name === name);
  return project ? project.id : null;
}

async function getCurrentProjectId(): Promise<string | null> {
  try {
    const response = await apiClient<any>('GET', '/projects/current');
    if (response.success && response.data) return response.data.id;
    return null;
  } catch {
    return null;
  }
}

export function setupAttachCommand(program: Command): void {
  program
    .command('attach')
    .description('Attach to agent events in real time')
    .option('-p, --project <name>', 'Filter by project name')
    .option('-f, --follow', 'Auto-reconnect on disconnect')
    .action(async (options) => {
      await requireServer();

      let projectId: string | undefined;

      if (options.project) {
        const id = await resolveProjectId(options.project);
        if (!id) {
          console.error(chalk.red(`Error: Project "${options.project}" not found`));
          process.exit(1);
        }
        projectId = id;
      } else {
        const currentId = await getCurrentProjectId();
        if (currentId) {
          projectId = currentId;
        }
      }

      let sseUrl = `${SSE_BASE}/api/events`;
      if (projectId) {
        sseUrl += `?projectId=${projectId}`;
      }

      let active = true;
      let currentReq: ClientRequest | null = null;

      const cleanup = () => {
        active = false;
        if (currentReq) {
          currentReq.destroy();
          currentReq = null;
        }
        console.log(chalk.gray('Detached.'));
        process.exit(0);
      };

      process.on('SIGINT', cleanup);
      process.on('SIGTERM', cleanup);

      const connect = () => {
        if (!active) return;

        currentReq = connectSSE(sseUrl, {
          onEvent(eventType, data) {
            console.log(formatEvent(eventType, data));
          },
          onError(err) {
            if (!active) return;
            console.error(chalk.red(`Connection error: ${err.message}`));
            if (options.follow) {
              console.log(chalk.yellow('Reconnecting...'));
              setTimeout(connect, 2000);
            } else {
              active = false;
              process.exit(1);
            }
          },
          onClose() {
            if (!active) return;
            if (options.follow) {
              console.log(chalk.yellow('Reconnecting...'));
              setTimeout(connect, 2000);
            }
          },
        });
      };

      connect();
    });
}
