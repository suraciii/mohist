import { spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import chalk from 'chalk';
import http from 'http';

const PID_FILE = path.join(process.env.HOME || '', '.mohist', 'server.pid');
const LOGS_DIR = path.join(process.env.HOME || '', '.mohist', 'logs');
const STDERR_LOG_FILE = path.join(LOGS_DIR, 'server.log');
const LOG_FILE_PATTERN = /^\d{4}-\d{2}-\d{2}T\d{6}\.log$/;

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

function getServerStatus(): ServerStatus {
  const status: ServerStatus = { running: false };
  
  if (fs.existsSync(PID_FILE)) {
    const pid = parseInt(fs.readFileSync(PID_FILE, 'utf-8').trim(), 10);
    
    try {
      process.kill(pid, 0);
      status.running = true;
      status.pid = pid;
      status.port = 3456;
      
      const stats = fs.statSync(PID_FILE);
      status.uptime = Date.now() - stats.birthtimeMs;
    } catch (error) {
      fs.unlinkSync(PID_FILE);
    }
  }
  
  return status;
}

export async function startServer(): Promise<void> {
  const status = getServerStatus();
  
  if (status.running) {
    console.log(chalk.yellow('Server is already running'));
    console.log(chalk.gray(`PID: ${status.pid}`));
    return;
  }
  
  const serverPath = path.join(__dirname, '..', '..', '..', 'bin', 'mo-server');
  
  if (!fs.existsSync(LOGS_DIR)) {
    fs.mkdirSync(LOGS_DIR, { recursive: true });
  }
  
  const stderrStream = fs.openSync(STDERR_LOG_FILE, 'a');
  
  const child = spawn(process.execPath, [serverPath], {
    detached: true,
    stdio: ['ignore', 'ignore', stderrStream],
    cwd: process.cwd()
  });
  
  child.unref();
  
  const pid = child.pid;
  if (!pid) {
    console.error(chalk.red('Failed to start server'));
    process.exit(1);
  }
  
  const pidDir = path.dirname(PID_FILE);
  if (!fs.existsSync(pidDir)) {
    fs.mkdirSync(pidDir, { recursive: true });
  }
  fs.writeFileSync(PID_FILE, pid.toString());
  
  console.log(chalk.blue('Starting server...'));
  
  let retries = 10;
  while (retries > 0) {
    await new Promise(resolve => setTimeout(resolve, 500));
    
    const isRunning = await checkServerHealth();
    if (isRunning) {
      console.log(chalk.green('Server started'));
      console.log(chalk.gray(`PID: ${pid}`));
      console.log(chalk.gray(`Port: 3456`));
      const latestLog = getLatestLogFile();
      console.log(chalk.gray(`Logs: ${latestLog || LOGS_DIR}`));
      return;
    }
    
    retries--;
  }
  
  console.error(chalk.red('Server failed to start within timeout'));
  console.log(chalk.yellow('Check logs at: ' + (getLatestLogFile() || STDERR_LOG_FILE)));
  process.exit(1);
}

export async function stopServer(): Promise<void> {
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

    console.log(chalk.gray(`Logs: ${getLatestLogFile() || LOGS_DIR}`));
  } else {
    console.log(chalk.red('Server is not running'));
    console.log(chalk.yellow('Start with: mo server start'));
  }
}

export async function serverLogs(lines: number = 50): Promise<void> {
  const logFile = getLatestLogFile();
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
