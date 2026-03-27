import { spawn, type SpawnOptions } from 'child_process';
import { z } from 'zod';
import { Tool, type ToolInstance } from '../agent-runtime/tool';

const DEFAULT_TIMEOUT = 30 * 60 * 1000;

interface SpawnAgentResult {
  success: boolean;
  stdout: string;
  stderr: string;
  exitCode: number | null;
}

function runSubprocess(
  command: string,
  args: string[],
  options: SpawnOptions,
  timeout: number
): Promise<SpawnAgentResult> {
  return new Promise((resolve) => {
    const proc = spawn(command, args, {
      ...options,
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let stdout = '';
    let stderr = '';

    proc.stdout?.on('data', (data: Buffer) => {
      stdout += data.toString();
    });

    proc.stderr?.on('data', (data: Buffer) => {
      stderr += data.toString();
    });

    const timer = setTimeout(() => {
      proc.kill('SIGTERM');
      const killTimer = setTimeout(() => {
        try {
          proc.kill('SIGKILL');
        } catch {
          // already exited
        }
      }, 5000);
      proc.on('exit', () => clearTimeout(killTimer));
      resolve({
        success: false,
        stdout: stdout.trim(),
        stderr: `Agent timed out after ${timeout / 1000}s`,
        exitCode: null,
      });
    }, timeout);

    proc.on('close', (code) => {
      clearTimeout(timer);
      resolve({
        success: code === 0,
        stdout: stdout.trim(),
        stderr: stderr.trim(),
        exitCode: code,
      });
    });

    proc.on('error', (error) => {
      clearTimeout(timer);
      resolve({
        success: false,
        stdout: '',
        stderr: `Failed to spawn process: ${error.message}`,
        exitCode: null,
      });
    });
  });
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function createSpawnAgentTool(defaultCwd?: string): ToolInstance<any> {
  return Tool.define('spawn_agent', {
    description:
      'Spawn an opencode subprocess to execute a task. The subprocess runs in the issue worktree directory. Returns stdout on success or stderr on failure.',
    parameters: z.object({
      agent_type: z
        .string()
        .describe(
          'The type of agent to spawn (e.g. "code" for code implementation)'
        ),
      task: z
        .string()
        .describe('The task description / prompt to send to the agent'),
      cwd: z
        .string()
        .optional()
        .describe(
          'Working directory for the subprocess. Defaults to the issue worktree.'
        ),
      timeout: z
        .number()
        .optional()
        .describe(
          'Timeout in milliseconds. Defaults to 30 minutes (1800000).'
        ),
    }),
    execute: async (params) => {
      const cwd = params.cwd ?? defaultCwd;
      if (!cwd) {
        return 'Error: no working directory specified. Provide cwd parameter or configure default.';
      }

      const timeout = params.timeout ?? DEFAULT_TIMEOUT;
      const args = ['agent', '--local', '--message', params.task];

      console.log(
        `[spawn_agent] Spawning: opencode ${args.join(' ')} (cwd=${cwd}, timeout=${timeout}ms)`
      );

      const result = await runSubprocess('opencode', args, { cwd }, timeout);

      if (result.success) {
        const output =
          result.stdout.length > 0
            ? result.stdout
            : '(agent completed with no output)';
        return `Success (exit code 0):\n${output}`;
      }

      if (result.exitCode === null) {
        return `Timeout after ${timeout / 1000}s.\nPartial stdout:\n${result.stdout || '(empty)'}\nPartial stderr:\n${result.stderr || '(empty)'}`;
      }

      return `Failed (exit code ${result.exitCode}):\n${result.stderr || '(no stderr)'}\nStdout:\n${result.stdout || '(empty)'}`;
    },
  });
}
