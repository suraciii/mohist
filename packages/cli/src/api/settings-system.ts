import { Hono } from 'hono';
import { spawn } from 'child_process';
import * as path from 'path';
import { detectInstallMode, isSystemdServiceInstalled, runSystemctlUserSafe } from '../cli/commands/server-systemd';
import type { ApiResponse } from '../types';

const SERVICE_NAME = 'mohist.service';

function spawnRebuild(onComplete: () => void, onFailure: (error: string) => void): void {
  const mode = detectInstallMode();
  if (!mode.workingDir) {
    onFailure('Not in source mode');
    return;
  }

  const cliDir = path.resolve(mode.workingDir, 'packages', 'cli');
  const webDir = path.resolve(cliDir, 'web');

  const proc1 = spawn('npm', ['run', 'build'], { cwd: cliDir, stdio: 'inherit' });
  proc1.on('close', (code1) => {
    if (code1 !== 0) {
      console.error('[rebuild] CLI build failed');
      onFailure('CLI build failed');
      return;
    }
    console.log('[rebuild] CLI build succeeded');

    const proc2 = spawn('npm', ['run', 'build'], { cwd: webDir, stdio: 'inherit' });
    proc2.on('close', (code2) => {
      if (code2 !== 0) {
        console.error('[rebuild] Web build failed');
        onFailure('Web build failed');
        return;
      }
      console.log('[rebuild] Web build succeeded');

      const res = runSystemctlUserSafe(`restart ${SERVICE_NAME}`);
      if (!res.success) {
        console.error(`[rebuild] systemd restart failed: ${res.error}`);
        onFailure(`systemd restart failed: ${res.error}`);
        return;
      }
      console.log('[rebuild] Server restarted (systemd)');
      onComplete();
    });
  });
}

export function createSettingsSystemRoutes(): Hono {
  const app = new Hono();

  app.post('/rebuild', (c) => {
    const mode = detectInstallMode();

    if (!mode.workingDir) {
      const response: ApiResponse = {
        success: false,
        error: 'Rebuild is only available in source mode',
      };
      return c.json(response, 400);
    }

    if (!isSystemdServiceInstalled()) {
      const response: ApiResponse = {
        success: false,
        error: 'systemd service is not installed',
      };
      return c.json(response, 400);
    }

    spawnRebuild(
      () => {
        console.log('[rebuild] Background rebuild completed successfully');
      },
      (error) => {
        console.error(`[rebuild] Background rebuild failed: ${error}`);
      }
    );

    const response: ApiResponse = {
      success: true,
    };
    return c.json(response, 200);
  });

  return app;
}