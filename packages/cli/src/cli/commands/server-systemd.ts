import { execSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import chalk from 'chalk';

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

export async function installSystemdService(): Promise<void> {
  const isReinstall = isSystemdServiceInstalled();

  const mode = detectInstallMode();
  console.log(chalk.blue('Detected install mode:'), mode.workingDir ? 'source' : 'npm global');

  const serviceContent = generateServiceFile(mode);

  if (!fs.existsSync(SYSTEMD_USER_DIR)) {
    fs.mkdirSync(SYSTEMD_USER_DIR, { recursive: true });
  }

  fs.writeFileSync(SERVICE_FILE_PATH, serviceContent, 'utf-8');
  console.log(chalk.gray(`Service file written to ${SERVICE_FILE_PATH}`));

  runSystemctlUser('daemon-reload');
  console.log(chalk.gray('Daemon reloaded'));

  if (isReinstall) {
    runSystemctlUser(`restart ${SERVICE_NAME}`);
    console.log(chalk.gray('Service restarted'));
  } else {
    runSystemctlUser(`enable ${SERVICE_NAME}`);
    runSystemctlUser(`start ${SERVICE_NAME}`);
    console.log(chalk.gray('Service enabled and started'));
  }

  runLinger();
  console.log(chalk.gray('Linger enabled'));

  console.log(chalk.green(`\nMohist service installed successfully`));
  console.log(chalk.gray(`Service: ${SERVICE_NAME}`));
  console.log(chalk.gray(`Status:  systemctl --user status ${SERVICE_NAME}`));
  console.log(chalk.gray(`Logs:    journalctl --user -u ${SERVICE_NAME} -f`));
}

export async function uninstallSystemdService(): Promise<void> {
  if (!isSystemdServiceInstalled()) {
    console.log(chalk.yellow('Service not installed'));
    return;
  }

  try {
    runSystemctlUser(`disable --now ${SERVICE_NAME}`);
  } catch {
    runSystemctlUser(`stop ${SERVICE_NAME}`);
    runSystemctlUser(`disable ${SERVICE_NAME}`);
  }
  console.log(chalk.gray('Service stopped and disabled'));

  fs.unlinkSync(SERVICE_FILE_PATH);
  console.log(chalk.gray('Service file removed'));

  runSystemctlUser('daemon-reload');
  console.log(chalk.gray('Daemon reloaded'));

  console.log(chalk.green(`\nMohist service uninstalled successfully`));
}

export async function restartServer(
  fallbackStop: () => Promise<void>,
  fallbackStart: () => Promise<void>,
): Promise<void> {
  if (isSystemdServiceInstalled()) {
    runSystemctlUser(`restart ${SERVICE_NAME}`);
    console.log(chalk.green('Server restarted (systemd)'));
    return;
  }

  await fallbackStop();
  await fallbackStart();
  console.log(chalk.green('Server restarted'));
}

export async function updateServer(): Promise<void> {
  const mode = detectInstallMode();
  if (!mode.workingDir) {
    console.error(chalk.red('mo server update is only available in source mode'));
    console.log(chalk.gray('In npm global mode, update via: npm update -g mohist'));
    process.exit(1);
  }

  if (!isSystemdServiceInstalled()) {
    console.error(chalk.red('No systemd service installed'));
    console.log(chalk.gray('Run: mo server install'));
    process.exit(1);
  }

  const cliDir = path.resolve(mode.workingDir, 'packages', 'cli');
  const webDir = path.resolve(cliDir, 'web');

  console.log(chalk.blue('Building CLI...'));
  try {
    execSync('npm run build', { cwd: cliDir, encoding: 'utf-8', stdio: 'inherit' });
  } catch {
    console.error(chalk.red('CLI build failed — aborting update'));
    process.exit(1);
  }
  console.log(chalk.green('CLI build succeeded'));

  console.log(chalk.blue('Building web...'));
  try {
    execSync('npm run build', { cwd: webDir, encoding: 'utf-8', stdio: 'inherit' });
  } catch {
    console.error(chalk.red('Web build failed — aborting update'));
    process.exit(1);
  }
  console.log(chalk.green('Web build succeeded'));

  runSystemctlUser(`restart ${SERVICE_NAME}`);
  console.log(chalk.green('Server restarted (systemd)'));
  console.log(chalk.green('\nUpdate complete'));
}
