import { Command } from 'commander';
import chalk from 'chalk';
import readline from 'readline';
import { connectSSE } from '../sse-client';
import type { ClientRequest } from 'http';
import { formatEvent } from '../event-formatter';
import { apiClient, API_BASE } from '../api-client';
import { requireServer } from '../server-check';

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

interface PausedIssue {
  issueId: string;
  issueNumber: number;
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
      let pausedIssue: PausedIssue | null = null;
      let lastMessage: string | null = null;

      const rl = readline.createInterface({
        input: process.stdin,
        output: process.stdout,
        prompt: chalk.cyan('> '),
      });

      rl.on('line', async (line) => {
        const trimmed = line.trim();
        if (!trimmed) {
          rl.prompt();
          return;
        }

        if (trimmed === 'quit' || trimmed === 'exit') {
          cleanup();
          return;
        }

        if (!pausedIssue) {
          console.log(chalk.yellow('No paused agent to send message to. Waiting for agent to pause...'));
          rl.prompt();
          return;
        }

        const issueNumber = pausedIssue.issueNumber;
        lastMessage = trimmed;
        try {
          const result = await apiClient<any>('POST', `/issues/${issueNumber}/messages`, {
            message: trimmed,
          });

          if (result.success) {
            const summary = trimmed.length > 50 ? trimmed.substring(0, 50) + '...' : trimmed;
            console.log(chalk.green(`Message sent to issue #${issueNumber}: "${summary}"`));
            pausedIssue = null;
            lastMessage = null;
          } else {
            if (result.error?.includes('not paused') || result.error?.includes('409')) {
              console.log(chalk.yellow(`Agent resumed before message could be sent. Wait for next pause.`));
              pausedIssue = null;
              lastMessage = null;
            } else {
              console.log(chalk.red(`Failed to send message: ${result.error}`));
              pausedIssue = null;
              lastMessage = null;
            }
          }
        } catch (err: any) {
          console.log(chalk.red(`Network error sending message: ${err.message}`));
          pausedIssue = null;
          lastMessage = null;
        }
      });

      rl.on('close', () => {
        cleanup();
      });

      function showPrompt(issueNumber: number) {
        console.log(chalk.yellow(`Agent paused for issue #${issueNumber}. Type a message to send, or 'quit' to detach.`));
        rl.prompt();
      }

      function hidePrompt() {
        if (pausedIssue) {
          readline.cursorTo(process.stdout, 0);
          readline.clearLine(process.stdout, 0);
          pausedIssue = null;
        }
      }

      const cleanup = () => {
        active = false;
        if (currentReq) {
          currentReq.destroy();
          currentReq = null;
        }
        rl.close();
        console.log(chalk.gray('Detached.'));
        process.exit(0);
      };

      process.on('SIGINT', cleanup);
      process.on('SIGTERM', cleanup);

      const connect = () => {
        if (!active) return;

        currentReq = connectSSE(sseUrl, {
          onEvent(eventType, data) {
            hidePrompt();
            console.log(formatEvent(eventType, data));

            let parsed: any;
            try {
              parsed = JSON.parse(data);
            } catch {
              return;
            }

            if (eventType === 'agent_paused' && parsed.issueNumber) {
              pausedIssue = {
                issueId: parsed.issueId,
                issueNumber: parsed.issueNumber,
              };
              showPrompt(pausedIssue.issueNumber);
            }

            if (eventType === 'agent_started' || eventType === 'agent_completed') {
              pausedIssue = null;
              lastMessage = null;
            }

            if (eventType === 'agent_error' && parsed.issueId && lastMessage) {
              console.log(chalk.red(`\nWarning: Your last message may not have been processed correctly.`));
              console.log(chalk.gray(`  Error: ${parsed.error || 'Unknown error'}`));
              lastMessage = null;
            }
          },
          onError(err) {
            if (!active) return;
            hidePrompt();
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
