import { execSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

const SERVICE_NAME = 'mohist.service';
const SYSTEMD_USER_DIR = path.join(os.homedir(), '.config', 'systemd', 'user');
const SERVICE_FILE_PATH = path.join(SYSTEMD_USER_DIR, SERVICE_NAME);

const DBUS_ERROR_PATTERNS = [
  'No session for user',
  'Failed to connect to bus',
  'Could not connect to D-Bus',
  'Cannot autolaunch D-Bus',
  'Not connected to D-Bus',
];

export function isSystemdServiceInstalled(): boolean {
  return fs.existsSync(SERVICE_FILE_PATH);
}

export interface InstallMode {
  nodePath: string;
  scriptPath: string;
  workingDir?: string;
}

export function detectInstallMode(): InstallMode {
  const nodePath = process.execPath;

  const distCliCommandsDir = __dirname;
  const distDir = path.resolve(distCliCommandsDir, '..');
  const srcCliDir = path.resolve(distDir, '..');
  const cliDir = path.resolve(srcCliDir, '..');
  const packagesCliDir = path.resolve(cliDir, '..');

  const binMoServer = path.join(packagesCliDir, 'bin', 'mo-server');
  if (fs.existsSync(binMoServer)) {
    const repoRoot = path.resolve(packagesCliDir, '..');
    return {
      nodePath,
      scriptPath: path.join(packagesCliDir, 'bin', 'mo-server'),
      workingDir: repoRoot,
    };
  }

  const globalScriptPath = path.resolve(distCliCommandsDir, '..', '..', 'bin', 'mo-server');
  return {
    nodePath,
    scriptPath: globalScriptPath,
  };
}

function validateNoCrlf(value: string, name: string): void {
  if (value.includes('\n') || value.includes('\r')) {
    throw new Error(`Invalid ${name}: contains newline characters`);
  }
}

function quoteIfNecessary(value: string): string {
  if (value.includes(' ') || value.includes('"') || value.includes("'")) {
    return `"${value.replace(/"/g, '\\"')}"`;
  }
  return value;
}

export interface ServiceFileOptions {
  nodePath: string;
  scriptPath: string;
  workingDir?: string;
}

export function generateServiceFile(options: ServiceFileOptions): string {
  const { nodePath, scriptPath, workingDir } = options;

  validateNoCrlf(nodePath, 'node path');
  validateNoCrlf(scriptPath, 'script path');
  if (workingDir) {
    validateNoCrlf(workingDir, 'working directory');
  }

  const execStart = `${quoteIfNecessary(nodePath)} ${quoteIfNecessary(scriptPath)} --print-logs`;

  const lines: string[] = [
    '[Unit]',
    'Description=Mohist AI Workflow Server',
    'After=network-online.target',
    '',
    '[Service]',
    'Type=simple',
    `ExecStart=${execStart}`,
    'Restart=on-failure',
    'RestartSec=5',
    'TimeoutStopSec=30',
    'SuccessExitStatus=0 143',
    'StandardError=journal',
  ];

  if (workingDir) {
    lines.push(`WorkingDirectory=${quoteIfNecessary(workingDir)}`);
  }

  lines.push(
    '',
    '[Install]',
    'WantedBy=default.target',
  );

  return lines.join('\n') + '\n';
}

export interface SystemdStatus {
  activeState: string;
  mainPID: number;
}

export function runSystemctlUser(args: string): string {
  try {
    return execSync(`systemctl --user ${args}`, {
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });
  } catch (err: any) {
    const stderr: string = err.stderr?.toString() || '';
    const isDbusError = DBUS_ERROR_PATTERNS.some(p => stderr.includes(p));

    if (!isDbusError) {
      throw err;
    }

    const username = os.userInfo().username;
    try {
      return execSync(`systemctl --machine ${username}@ --user ${args}`, {
        encoding: 'utf-8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
    } catch (retryErr: any) {
      if (process.env.SSH_CONNECTION) {
        const retryStderr: string = retryErr.stderr?.toString() || '';
        console.log(
          `[headless SSH detected] systemctl --user failed:\n${stderr.trim()}\n--machine retry also failed:\n${retryStderr.trim()}`,
        );
      }
      throw retryErr;
    }
  }
}

export function runLinger(): void {
  const username = os.userInfo().username;
  try {
    execSync(`loginctl enable-linger ${username}`, {
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });
  } catch (err: any) {
    const stderr: string = err.stderr?.toString() || '';
    if (!stderr.includes('already') && !stderr.includes('Failed to enable linger')) {
      return;
    }
  }
}

export function getSystemdStatus(): SystemdStatus | null {
  try {
    const output = runSystemctlUser(`show ${SERVICE_NAME}`);
    const activeStateMatch = output.match(/^ActiveState=(.+)$/m);
    const mainPIDMatch = output.match(/^MainPID=(\d+)$/m);

    const loadedMatch = output.match(/^Loaded=(.+)$/m);
    if (loadedMatch && loadedMatch[1].trim() === '') {
      return null;
    }

    return {
      activeState: activeStateMatch ? activeStateMatch[1].trim() : 'unknown',
      mainPID: mainPIDMatch ? parseInt(mainPIDMatch[1], 10) : 0,
    };
  } catch {
    return null;
  }
}
