import { spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import chalk from 'chalk';
import http from 'http';
import {
  restartServer as restartServerSystemd,
  updateServer as updateServerSystemd,
} from './server-systemd';

const PID_FILE = path.join(process.env.HOME || '', '.mohist', 'server.pid');
const RUNNER_PID_FILE = path.join(process.env.HOME || '', '.mohist', 'runner.pid');
const LOGS_DIR = path.join(process.env.HOME || '', '.mohist', 'logs');
const STDERR_LOG_FILE = path.join(LOGS_DIR, 'server.log');
const RUNNER_LOG_FILE = path.join(LOGS_DIR, 'runner.log');
const LOG_FILE_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{6}\.log$/;
const SERVER_URL = 'http://localhost:3456';

function getLatestLogFile(): string | null {
  if (!fs.existsSync(LOGS_DIR)) return null;
  const files = fs.readdirSync(LOGS_DIR)
    .filter(f => LOG_FILE_PATTERN.test(f))
    .sort();
  return files.length > 0 ? path.join(LOGS_DIR, files[files.length - 1]) : null;
}

interface ServerStatus {
  running: boolean;
  pid?: number;
  port?: number;
  uptime?: number;
}

interface RuntimeCommand {
  command: string;
  args: string[];
  cwd: string;
}

interface MohistRuntimeCommands {
  server: RuntimeCommand;
  runner: RuntimeCommand;
}

interface HealthResponse {
  status: string;
  timestamp: string;
  version: string | null;
  gitHash: string | null;
}

async function checkServerHealth(): Promise<boolean> {
  return new Promise((resolve) => {
    const req = http.get('http://localhost:3456/api/health', (res) => {
      resolve(res.statusCode === 200);
    });
    
    req.on('error', () => resolve(false));
    req.setTimeout(2000, () => {
      req.destroy();
      resolve(false);
    });
  });
}

export function findMohistRepoRoot(start: string = __dirname): string | null {
  let dir = path.resolve(start);
  while (true) {
    const serverProject = path.join(dir, 'packages', 'server', 'src', 'Mohist.Server', 'Mohist.Server.csproj');
    const runnerProject = path.join(dir, 'packages', 'runner', 'src', 'Mohist.Runner', 'Mohist.Runner.csproj');
    if (fs.existsSync(serverProject) && fs.existsSync(runnerProject)) {
      return dir;
    }

    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

export function resolveRuntimeCommands(start: string = __dirname): MohistRuntimeCommands {
  const repoRoot = findMohistRepoRoot(start);
  if (!repoRoot) {
    throw new Error('Unable to locate Mohist .NET runtime projects. Run from a source checkout or install a packaged runtime.');
  }

  return {
    server: {
      command: 'dotnet',
      args: ['run', '--project', path.join(repoRoot, 'packages', 'server', 'src', 'Mohist.Server', 'Mohist.Server.csproj')],
      cwd: repoRoot,
    },
    runner: {
      command: 'dotnet',
      args: [
        'run',
        '--project',
        path.join(repoRoot, 'packages', 'runner', 'src', 'Mohist.Runner', 'Mohist.Runner.csproj'),
        '--',
        `--ServerUrl=${SERVER_URL}`,
        `--RunnerId=mohist-local-${process.env.USER || 'user'}`,
      ],
      cwd: repoRoot,
    },
  };
}

export function getPidFileStatus(pidFile: string): ServerStatus {
  const status: ServerStatus = { running: false };

  if (!fs.existsSync(pidFile)) return status;

  const pid = parseInt(fs.readFileSync(pidFile, 'utf-8').trim(), 10);
  try {
    process.kill(pid, 0);
    status.running = true;
    status.pid = pid;
    status.port = pidFile === PID_FILE ? 3456 : undefined;
    const stats = fs.statSync(pidFile);
    status.uptime = Date.now() - stats.birthtimeMs;
  } catch {
    fs.unlinkSync(pidFile);
  }

  return status;
}

function spawnDetached(name: string, runtime: RuntimeCommand, pidFile: string, logFile: string): number {
  if (!fs.existsSync(LOGS_DIR)) {
    fs.mkdirSync(LOGS_DIR, { recursive: true });
  }
  const pidDir = path.dirname(pidFile);
  if (!fs.existsSync(pidDir)) {
    fs.mkdirSync(pidDir, { recursive: true });
  }

  const logStream = fs.openSync(logFile, 'a');
  const child = spawn(runtime.command, runtime.args, {
    detached: true,
    stdio: ['ignore', logStream, logStream],
    cwd: runtime.cwd,
    env: { ...process.env, ASPNETCORE_URLS: SERVER_URL },
  });

  child.unref();
  if (!child.pid) {
    throw new Error(`Failed to start ${name}`);
  }

  fs.writeFileSync(pidFile, child.pid.toString());
  return child.pid;
}

function ensureRunner(commands: MohistRuntimeCommands): void {
  const runnerStatus = getPidFileStatus(RUNNER_PID_FILE);
  if (!runnerStatus.running) {
    try {
      const runnerPid = spawnDetached('runner', commands.runner, RUNNER_PID_FILE, RUNNER_LOG_FILE);
      console.log(chalk.green('Runner started'));
      console.log(chalk.gray(`Runner PID: ${runnerPid}`));
      console.log(chalk.gray(`Runner logs: ${RUNNER_LOG_FILE}`));
    } catch (error: any) {
      console.log(chalk.yellow(`Warning: failed to start local runner: ${error.message || error}`));
    }
  } else {
    console.log(chalk.yellow('Runner is already running'));
    console.log(chalk.gray(`Runner PID: ${runnerStatus.pid}`));
  }
}

function stopPidFile(pidFile: string, label: string): boolean {
  const status = getPidFileStatus(pidFile);
  if (!status.running || !status.pid) {
    return false;
  }

  try {
    process.kill(status.pid, 'SIGTERM');
    if (fs.existsSync(pidFile)) fs.unlinkSync(pidFile);
    return true;
  } catch {
    if (fs.existsSync(pidFile)) fs.unlinkSync(pidFile);
    console.log(chalk.yellow(`${label} was not running; cleaned stale pid file`));
    return false;
  }
}

async function fetchVersionFromHealth(): Promise<string | null> {
  return new Promise((resolve) => {
    const req = http.get('http://localhost:3456/api/health', (res) => {
      let body = '';
      res.on('data', (chunk) => { body += chunk; });
      res.on('end', () => {
        try {
          const data: HealthResponse = JSON.parse(body);
          if (data.version && data.gitHash) {
            resolve(`${data.version} (${data.gitHash})`);
          } else if (data.version) {
            resolve(data.version);
          } else {
            resolve(null);
          }
        } catch {
          resolve(null);
        }
      });
    });

    req.on('error', () => resolve(null));
    req.setTimeout(2000, () => {
      req.destroy();
      resolve(null);
    });
  });
}

function getServerStatus(): ServerStatus {
  return getPidFileStatus(PID_FILE);
}

export async function startServer(): Promise<void> {
  const status = getServerStatus();
  
  if (status.running) {
    console.log(chalk.yellow('Server is already running'));
    console.log(chalk.gray(`PID: ${status.pid}`));
    try {
      ensureRunner(resolveRuntimeCommands());
    } catch (error: any) {
      console.log(chalk.yellow(`Warning: failed to ensure local runner: ${error.message || error}`));
    }
    return;
  }
  
  let commands: MohistRuntimeCommands;
  try {
    commands = resolveRuntimeCommands();
  } catch (error: any) {
    console.error(chalk.red(error.message || String(error)));
    process.exit(1);
  }

  const pid = spawnDetached('server', commands.server, PID_FILE, STDERR_LOG_FILE);
  
  console.log(chalk.blue('Starting server...'));
  
  let retries = 10;
  while (retries > 0) {
    await new Promise(resolve => setTimeout(resolve, 500));
    
    const isRunning = await checkServerHealth();
    if (isRunning) {
      console.log(chalk.green('Server started'));
      console.log(chalk.gray(`PID: ${pid}`));
      console.log(chalk.gray(`Port: 3456`));
      console.log(chalk.gray(`Logs: ${STDERR_LOG_FILE}`));

      ensureRunner(commands);
      return;
    }
    
    retries--;
  }
  
  console.error(chalk.red('Server failed to start within timeout'));
  console.log(chalk.yellow('Check logs at: ' + (getLatestLogFile() || STDERR_LOG_FILE)));
  process.exit(1);
}

export async function stopServer(): Promise<void> {
  const runnerStopped = stopPidFile(RUNNER_PID_FILE, 'Runner');
  if (runnerStopped) {
    console.log(chalk.green('Runner stopped'));
  }

  const status = getServerStatus();
  
  if (!status.running || !status.pid) {
    console.log(chalk.yellow('Server is not running'));
    return;
  }
  
  console.log(chalk.blue('Stopping server...'));
  
  try {
    process.kill(status.pid, 'SIGTERM');
    
    let retries = 10;
    while (retries > 0) {
      await new Promise(resolve => setTimeout(resolve, 500));
      
      try {
        process.kill(status.pid, 0);
        retries--;
      } catch (error) {
        if (fs.existsSync(PID_FILE)) {
          fs.unlinkSync(PID_FILE);
        }
        console.log(chalk.green('Server stopped'));
        return;
      }
    }
    
    console.log(chalk.yellow('Server did not stop gracefully, forcing...'));
    process.kill(status.pid, 'SIGKILL');
    
    if (fs.existsSync(PID_FILE)) {
      fs.unlinkSync(PID_FILE);
    }
    
    console.log(chalk.green('Server stopped (forced)'));
  } catch (error) {
    console.error(chalk.red('Failed to stop server'));
    if (fs.existsSync(PID_FILE)) {
      fs.unlinkSync(PID_FILE);
    }
    process.exit(1);
  }
}

export async function serverStatus(): Promise<void> {
  const status = getServerStatus();
  
  if (status.running) {
    console.log(chalk.green('Server is running'));
    console.log(chalk.gray(`PID: ${status.pid}`));
    console.log(chalk.gray(`Port: ${status.port}`));
    const runnerStatus = getPidFileStatus(RUNNER_PID_FILE);
    if (runnerStatus.running) {
      console.log(chalk.green('Runner is running'));
      console.log(chalk.gray(`Runner PID: ${runnerStatus.pid}`));
    } else {
      console.log(chalk.yellow('Runner is not running'));
    }
    
    if (status.uptime) {
      const seconds = Math.floor(status.uptime / 1000);
      const minutes = Math.floor(seconds / 60);
      const hours = Math.floor(minutes / 60);
      
      if (hours > 0) {
        console.log(chalk.gray(`Uptime: ${hours}h ${minutes % 60}m`));
      } else if (minutes > 0) {
        console.log(chalk.gray(`Uptime: ${minutes}m ${seconds % 60}s`));
      } else {
        console.log(chalk.gray(`Uptime: ${seconds}s`));
      }
    }

    const versionStr = await fetchVersionFromHealth();
    if (versionStr) {
      console.log(chalk.gray(`Version: ${versionStr}`));
    }

    console.log(chalk.gray(`Logs: ${STDERR_LOG_FILE}`));
  } else {
    console.log(chalk.red('Server is not running'));
    console.log(chalk.yellow('Start with: mo server start'));
  }
}

export async function serverLogs(lines: number = 50): Promise<void> {
  const logFile = getLatestLogFile() || (fs.existsSync(STDERR_LOG_FILE) ? STDERR_LOG_FILE : null);
  if (!logFile || !fs.existsSync(logFile)) {
    console.log(chalk.yellow('No logs found'));
    console.log(chalk.gray('Server may not have been started yet'));
    return;
  }
  
  const content = fs.readFileSync(logFile, 'utf-8');
  const logLines = content.split('\n').filter(line => line.trim());
  const lastLines = logLines.slice(-lines);
  
  console.log(chalk.blue(`Last ${lines} lines from server log:`));
  console.log(chalk.gray('─'.repeat(60)));
  
  lastLines.forEach(line => {
    console.log(line);
  });
  
  console.log(chalk.gray('─'.repeat(60)));
  console.log(chalk.gray(`Full log: ${logFile}`));
}

export async function restartServerCommand(): Promise<void> {
  await restartServerSystemd(stopServer, startServer);
}

export async function updateServerCommand(): Promise<void> {
  await updateServerSystemd();
}
