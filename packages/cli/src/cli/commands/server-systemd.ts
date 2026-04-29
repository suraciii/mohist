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
  'No medium found',
];

function buildSystemctlEnv(): NodeJS.ProcessEnv {
  const env = { ...process.env };

  const uid = typeof process.getuid === 'function' ? process.getuid() : null;
  if (uid === null) {
    return env;
  }

  const runtimeDir = `/run/user/${uid}`;
  const busSocket = `${runtimeDir}/bus`;

  if (!env.DBUS_SESSION_BUS_ADDRESS && fs.existsSync(busSocket)) {
    env.DBUS_SESSION_BUS_ADDRESS = `unix:path=${busSocket}`;
  }

  if (!env.XDG_RUNTIME_DIR && fs.existsSync(runtimeDir)) {
    env.XDG_RUNTIME_DIR = runtimeDir;
  }

  return env;
}

function printHeadlessDbusHint(): void {
  const uid = typeof process.getuid === 'function' ? process.getuid() : null;
  const socketPath = uid !== null ? `/run/user/${uid}/bus` : null;

  if (socketPath && fs.existsSync(socketPath)) {
    console.log(chalk.yellow('\nD-Bus session bus is unavailable but the socket exists.'));
    console.log(chalk.gray('  export DBUS_SESSION_BUS_ADDRESS=unix:path=') + socketPath);
    console.log(chalk.gray('  export XDG_RUNTIME_DIR=/run/user/') + uid);
  } else {
    console.log(chalk.yellow('\nD-Bus session bus is unavailable. Ensure systemd --user is running.'));
  }
}

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

  const commandsDir = __dirname;
  const cliPkgDir = path.resolve(commandsDir, '..', '..', '..');

  const binMoServer = path.join(cliPkgDir, 'bin', 'mo-server');
  if (fs.existsSync(binMoServer)) {
    const repoRoot = path.resolve(cliPkgDir, '..', '..');
    return {
      nodePath,
      scriptPath: binMoServer,
      workingDir: repoRoot,
    };
  }

  const globalScriptPath = path.join(cliPkgDir, 'bin', 'mo-server');
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

export interface SystemctlResult {
  success: boolean;
  output: string;
  error?: string;
}

function isDbusUnavailableError(stderr: string): boolean {
  return DBUS_ERROR_PATTERNS.some(p => stderr.includes(p));
}

export function runSystemctlUser(args: string): string {
  const env = buildSystemctlEnv();

  try {
    return execSync(`systemctl --user ${args}`, {
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
      env,
    });
  } catch (err: any) {
    const stderr: string = err.stderr?.toString() || '';

    if (!isDbusUnavailableError(stderr)) {
      throw err;
    }

    // Fallback: try --machine user@ --user (works in some sudo/headless setups)
    const username = os.userInfo().username;
    try {
      return execSync(`systemctl --machine ${username}@ --user ${args}`, {
        encoding: 'utf-8',
        stdio: ['pipe', 'pipe', 'pipe'],
        env,
      });
    } catch (retryErr: any) {
      const retryStderr: string = retryErr.stderr?.toString() || '';

      if (isDbusUnavailableError(retryStderr)) {
        printHeadlessDbusHint();
        throw new Error(
          `systemctl --user unavailable: D-Bus session bus not found. ` +
          `Set DBUS_SESSION_BUS_ADDRESS or ensure systemd --user is running.`,
        );
      }

      throw retryErr;
    }
  }
}

export function runSystemctlUserSafe(args: string): SystemctlResult {
  try {
    const output = runSystemctlUser(args);
    return { success: true, output };
  } catch (err: any) {
    return { success: false, output: '', error: err.message || String(err) };
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
    if (stderr.includes('already enabled') || stderr.includes('Already enabled')) {
      return;
    }
    console.log(chalk.yellow(`Warning: could not enable linger: ${stderr.trim()}`));
  }
}

export function getSystemdStatus(): SystemdStatus | null {
  const result = runSystemctlUserSafe(`show ${SERVICE_NAME}`);
  if (!result.success) {
    return null;
  }

  const output = result.output;
  const activeStateMatch = output.match(/^ActiveState=(.+)$/m);
  const mainPIDMatch = output.match(/^MainPID=(\d+)$/m);

  const loadStateMatch = output.match(/^LoadState=(.+)$/m);
  if (!loadStateMatch || loadStateMatch[1].trim() !== 'loaded') {
    return null;
  }

  return {
    activeState: activeStateMatch ? activeStateMatch[1].trim() : 'unknown',
    mainPID: mainPIDMatch ? parseInt(mainPIDMatch[1], 10) : 0,
  };
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

  let reload = runSystemctlUserSafe('daemon-reload');
  if (!reload.success) {
    console.error(chalk.red(`daemon-reload failed: ${reload.error}`));
    throw new Error('Install aborted: systemd daemon-reload failed');
  }
  console.log(chalk.gray('Daemon reloaded'));

  if (isReinstall) {
    const restart = runSystemctlUserSafe(`restart ${SERVICE_NAME}`);
    if (!restart.success) {
      console.error(chalk.red(`restart failed: ${restart.error}`));
      throw new Error('Install aborted: service restart failed');
    }
    console.log(chalk.gray('Service restarted'));
  } else {
    const enable = runSystemctlUserSafe(`enable ${SERVICE_NAME}`);
    if (!enable.success) {
      console.error(chalk.red(`enable failed: ${enable.error}`));
      throw new Error('Install aborted: service enable failed');
    }
    console.log(chalk.gray('Service enabled'));

    const start = runSystemctlUserSafe(`start ${SERVICE_NAME}`);
    if (!start.success) {
      console.error(chalk.red(`start failed: ${start.error}`));
      throw new Error('Install aborted: service start failed');
    }
    console.log(chalk.gray('Service started'));
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

  const disableNow = runSystemctlUserSafe(`disable --now ${SERVICE_NAME}`);
  if (!disableNow.success) {
    runSystemctlUserSafe(`stop ${SERVICE_NAME}`);
    runSystemctlUserSafe(`disable ${SERVICE_NAME}`);
  }
  console.log(chalk.gray('Service stopped and disabled'));

  fs.unlinkSync(SERVICE_FILE_PATH);
  console.log(chalk.gray('Service file removed'));

  const reload = runSystemctlUserSafe('daemon-reload');
  if (reload.success) {
    console.log(chalk.gray('Daemon reloaded'));
  }

  console.log(chalk.green(`\nMohist service uninstalled successfully`));
}

export async function restartServer(
  fallbackStop: () => Promise<void>,
  fallbackStart: () => Promise<void>,
): Promise<void> {
  if (isSystemdServiceInstalled()) {
    const res = runSystemctlUserSafe(`restart ${SERVICE_NAME}`);
    if (!res.success) {
      console.error(chalk.red(`systemd restart failed: ${res.error}`));
      console.log(chalk.gray('Falling back to manual restart...'));
      await fallbackStop();
      await fallbackStart();
      console.log(chalk.green('Server restarted (fallback)'));
      return;
    }
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

  const res = runSystemctlUserSafe(`restart ${SERVICE_NAME}`);
  if (!res.success) {
    console.error(chalk.red(`systemd restart failed: ${res.error}`));
    process.exit(1);
  }
  console.log(chalk.green('Server restarted (systemd)'));
  console.log(chalk.green('\nUpdate complete'));
}
