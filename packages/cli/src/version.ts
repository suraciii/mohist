import { execSync } from 'child_process';
import * as path from 'path';
import * as fs from 'fs';
import { detectInstallMode } from './cli/commands/server-systemd';

export interface VersionInfo {
  version: string;
  gitHash: string | null;
  versionString: string;
}

let cached: VersionInfo | null = null;

export function getVersionInfo(): VersionInfo {
  if (cached) return cached;

  const pkgPath = path.join(__dirname, '..', 'package.json');
  const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf-8'));
  const version: string = pkg.version || '0.0.0';

  let gitHash: string | null = null;
  try {
    const repoRoot = path.join(__dirname, '..', '..');
    gitHash = execSync('git rev-parse --short HEAD', {
      cwd: repoRoot,
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
    }).trim();
  } catch {
    gitHash = null;
  }

  const versionString = gitHash ? `${version} (${gitHash})` : version;

  cached = { version, gitHash, versionString };
  return cached;
}

export function getSourceHead(): string | null {
  const mode = detectInstallMode();
  if (!mode.workingDir) return null;

  try {
    return execSync('git rev-parse --short HEAD', {
      cwd: mode.workingDir,
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
    }).trim();
  } catch {
    return null;
  }
}
