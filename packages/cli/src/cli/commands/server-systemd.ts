import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

const SERVICE_NAME = 'mohist.service';
const SYSTEMD_USER_DIR = path.join(os.homedir(), '.config', 'systemd', 'user');
const SERVICE_FILE_PATH = path.join(SYSTEMD_USER_DIR, SERVICE_NAME);

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
