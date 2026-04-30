import { Hono } from 'hono';
import { ApiResponse, ConfigConflictError } from '../types';
import {
  load,
  writeConfig,
  getConfigPath,
  resolveOpencodeBinPath,
  getServerConfig,
  clearConfigCache,
} from '../config/config-loader';
import { execSync } from 'node:child_process';
import * as path from 'node:path';
import * as os from 'node:os';
import * as fs from 'node:fs';

const VALID_LOG_LEVELS = ['DEBUG', 'INFO', 'WARN', 'ERROR'] as const;

const MODEL_REGEX = /^[^/]+\/.+$/;

const AGENT_RUNTIME_DEFAULTS = {
  timeout: 1800000,
  stageTimeout: 3600000,
  taskTimeout: 600000,
  maxConcurrent: 8,
  maxGracePeriods: 2,
  pollInterval: 30000,
};

type AgentRuntimeField = keyof typeof AGENT_RUNTIME_DEFAULTS;

const AGENT_RUNTIME_VALIDATORS: Record<AgentRuntimeField, (v: unknown) => string | null> = {
  timeout: (v) => (typeof v !== 'number' || v <= 0 ? 'timeout must be a positive number' : null),
  stageTimeout: (v) => (typeof v !== 'number' || v <= 0 ? 'stageTimeout must be a positive number' : null),
  taskTimeout: (v) => (typeof v !== 'number' || v <= 0 ? 'taskTimeout must be a positive number' : null),
  maxConcurrent: (v) => (typeof v !== 'number' || v < 1 ? 'maxConcurrent must be >= 1' : null),
  maxGracePeriods: (v) => (typeof v !== 'number' || v < 0 ? 'maxGracePeriods must be >= 0' : null),
  pollInterval: (v) => (typeof v !== 'number' || v <= 0 ? 'pollInterval must be a positive number' : null),
};

function validateModel(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== 'string') return 'model must be a string or null';
  if (!MODEL_REGEX.test(value)) return 'Invalid model format. Expected "provider/model"';
  return null;
}

function getGitHash(): string {
  try {
    return execSync('git rev-parse --short HEAD', {
      encoding: 'utf-8',
      stdio: ['pipe', 'pipe', 'pipe'],
      timeout: 3000,
    }).trim();
  } catch {
    return 'unknown';
  }
}

function getVersion(): string {
  try {
    const pkgPath = path.join(__dirname, '..', '..', 'package.json');
    const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf-8'));
    return pkg.version ?? 'unknown';
  } catch {
    return 'unknown';
  }
}

export function createSettingsConfigRoutes(serverConfig?: { host: string; port: number }): Hono {
  const app = new Hono();

  app.get('/system/info', (c) => {
    const config = load();
    const serverCfg = serverConfig ?? getServerConfig(config);
    const opencodeBin = resolveOpencodeBinPath(config);

    const homeDir = os.homedir();
    const mohistDir = path.join(homeDir, '.mohist');

    const info = {
      version: getVersion(),
      gitHash: getGitHash(),
      server: {
        host: serverCfg.host,
        port: serverCfg.port,
        status: 'running' as const,
      },
      paths: {
        db: path.join(mohistDir, 'mohist.db'),
        config: getConfigPath(),
        opencode: opencodeBin ?? null,
        logs: path.join(mohistDir, 'logs'),
      },
    };

    const response: ApiResponse<typeof info> = { success: true, data: info };
    return c.json(response);
  });

  app.get('/model', (c) => {
    const config = load();
    const response: ApiResponse<{ model: string | null }> = {
      success: true,
      data: { model: config.model ?? null },
    };
    return c.json(response);
  });

  app.put('/model', async (c) => {
    try {
      const body = await c.req.json();
      const err = validateModel(body.model);
      if (err) {
        return c.json<ApiResponse>({ success: false, error: err }, 400);
      }

      clearConfigCache();
      const config = load();
      if (body.model === null) {
        delete config.model;
      } else {
        config.model = body.model;
      }
      writeConfig(config);

      const response: ApiResponse<{ model: string | null }> = {
        success: true,
        data: { model: config.model ?? null },
      };
      return c.json(response);
    } catch (error) {
      if (error instanceof ConfigConflictError) {
        return c.json<ApiResponse>(
          { success: false, error: 'Configuration version conflict' },
          409,
        );
      }
      return c.json<ApiResponse>(
        { success: false, error: error instanceof Error ? error.message : 'Unknown error' },
        500,
      );
    }
  });

  app.get('/opencode-model', (c) => {
    const config = load();
    const response: ApiResponse<{ model: string | null }> = {
      success: true,
      data: { model: config.opencode?.model ?? null },
    };
    return c.json(response);
  });

  app.put('/opencode-model', async (c) => {
    try {
      const body = await c.req.json();
      const err = validateModel(body.model);
      if (err) {
        return c.json<ApiResponse>({ success: false, error: err }, 400);
      }

      clearConfigCache();
      const config = load();
      if (!config.opencode) config.opencode = {};

      if (body.model === null) {
        delete config.opencode.model;
        if (Object.keys(config.opencode).length === 0) delete config.opencode;
      } else {
        config.opencode.model = body.model;
      }
      writeConfig(config);

      const response: ApiResponse<{ model: string | null }> = {
        success: true,
        data: { model: config.opencode?.model ?? null },
      };
      return c.json(response);
    } catch (error) {
      if (error instanceof ConfigConflictError) {
        return c.json<ApiResponse>(
          { success: false, error: 'Configuration version conflict' },
          409,
        );
      }
      return c.json<ApiResponse>(
        { success: false, error: error instanceof Error ? error.message : 'Unknown error' },
        500,
      );
    }
  });

  app.get('/log-level', (c) => {
    const config = load();
    const response: ApiResponse<{ level: string }> = {
      success: true,
      data: { level: config.log?.level ?? 'INFO' },
    };
    return c.json(response);
  });

  app.put('/log-level', async (c) => {
    try {
      const body = await c.req.json();
      if (typeof body.level !== 'string' || !VALID_LOG_LEVELS.includes(body.level as any)) {
        return c.json<ApiResponse>(
          { success: false, error: `Invalid log level. Must be one of: ${VALID_LOG_LEVELS.join(', ')}` },
          400,
        );
      }

      clearConfigCache();
      const config = load();
      if (!config.log) config.log = { level: body.level };
      else config.log.level = body.level as any;
      writeConfig(config);

      const response: ApiResponse<{ level: string }> = {
        success: true,
        data: { level: body.level },
      };
      return c.json(response);
    } catch (error) {
      if (error instanceof ConfigConflictError) {
        return c.json<ApiResponse>(
          { success: false, error: 'Configuration version conflict' },
          409,
        );
      }
      return c.json<ApiResponse>(
        { success: false, error: error instanceof Error ? error.message : 'Unknown error' },
        500,
      );
    }
  });

  app.get('/agent-runtime', (c) => {
    const config = load();
    const agent = config.agent ?? {};
    const data: Record<string, number> = {};
    for (const key of Object.keys(AGENT_RUNTIME_DEFAULTS) as AgentRuntimeField[]) {
      data[key] = agent[key] ?? AGENT_RUNTIME_DEFAULTS[key];
    }

    const response: ApiResponse<typeof data> = { success: true, data };
    return c.json(response);
  });

  app.put('/agent-runtime', async (c) => {
    try {
      const body = await c.req.json();
      if (!body || typeof body !== 'object') {
        return c.json<ApiResponse>({ success: false, error: 'Request body must be an object' }, 400);
      }

      const updates = body as Record<string, unknown>;
      const validKeys = new Set<string>(Object.keys(AGENT_RUNTIME_DEFAULTS));

      for (const [key, value] of Object.entries(updates)) {
        if (!validKeys.has(key)) continue;
        const validator = AGENT_RUNTIME_VALIDATORS[key as AgentRuntimeField];
        const err = validator(value);
        if (err) {
          return c.json<ApiResponse>({ success: false, error: `${key}: ${err}` }, 400);
        }
      }

      clearConfigCache();
      const config = load();
      if (!config.agent) config.agent = {};

      for (const [key, value] of Object.entries(updates)) {
        if (validKeys.has(key)) {
          (config.agent as Record<string, number>)[key] = value as number;
        }
      }
      writeConfig(config);

      const data: Record<string, number> = {};
      for (const key of Object.keys(AGENT_RUNTIME_DEFAULTS) as AgentRuntimeField[]) {
        data[key] = config.agent![key] ?? AGENT_RUNTIME_DEFAULTS[key];
      }

      const response: ApiResponse<typeof data> = { success: true, data };
      return c.json(response);
    } catch (error) {
      if (error instanceof ConfigConflictError) {
        return c.json<ApiResponse>(
          { success: false, error: 'Configuration version conflict' },
          409,
        );
      }
      return c.json<ApiResponse>(
        { success: false, error: error instanceof Error ? error.message : 'Unknown error' },
        500,
      );
    }
  });

  app.get('/stage-models', (c) => {
    const config = load();
    const stageModels = config.opencode?.stageModels ?? null;
    const response: ApiResponse<{ stageModels: Record<string, string> | null }> = {
      success: true,
      data: { stageModels },
    };
    return c.json(response);
  });

  app.put('/stage-models', async (c) => {
    try {
      const body = await c.req.json();

      if (body.stageModels !== null && body.stageModels !== undefined) {
        if (typeof body.stageModels !== 'object' || Array.isArray(body.stageModels)) {
          return c.json<ApiResponse>(
            { success: false, error: 'stageModels must be an object or null' },
            400,
          );
        }

        for (const [stage, model] of Object.entries(body.stageModels as Record<string, string>)) {
          if (typeof model !== 'string' || !MODEL_REGEX.test(model)) {
            return c.json<ApiResponse>(
              { success: false, error: `Invalid model format for stage "${stage}". Expected "provider/model"` },
              400,
            );
          }
        }
      }

      clearConfigCache();
      const config = load();
      if (!config.opencode) config.opencode = {};

      if (body.stageModels === null) {
        delete config.opencode.stageModels;
        if (Object.keys(config.opencode).length === 0) delete config.opencode;
      } else {
        config.opencode.stageModels = body.stageModels;
      }
      writeConfig(config);

      const response: ApiResponse<{ stageModels: Record<string, string> | null }> = {
        success: true,
        data: { stageModels: config.opencode?.stageModels ?? null },
      };
      return c.json(response);
    } catch (error) {
      if (error instanceof ConfigConflictError) {
        return c.json<ApiResponse>(
          { success: false, error: 'Configuration version conflict' },
          409,
        );
      }
      return c.json<ApiResponse>(
        { success: false, error: error instanceof Error ? error.message : 'Unknown error' },
        500,
      );
    }
  });

  return app;
}
