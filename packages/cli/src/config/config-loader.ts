import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';
import { execFileSync } from 'node:child_process';
import * as jsonc from 'jsonc-parser';
import { ConfigInfoSchema, type ConfigInfo } from './config-schema';
import { BUILTIN_PROVIDERS, type BuiltinProvider } from './builtin-providers';
import { ConfigConflictError } from '../types';

export interface ResolvedProvider {
  sdk: string;
  name: string;
  apiKey: string | null;
  baseURL: string | null;
  envVars: string[];
  source: 'config' | 'env' | 'builtin' | 'none';
}

const CONFIG_DIR = (): string => path.join(os.homedir(), '.mohist');
const CONFIG_PATH = (): string => path.join(CONFIG_DIR(), 'config.jsonc');

const configCache = new Map<string, ConfigInfo>();

export function clearConfigCache(): void {
  configCache.clear();
}

function ensureConfigDir(): void {
  const dir = CONFIG_DIR();
  if (!fs.existsSync(dir)) {
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
  }
}

export function load(configPath?: string): ConfigInfo {
  const filePath = configPath ?? CONFIG_PATH();

  if (configCache.has(filePath)) {
    return configCache.get(filePath)!;
  }

  if (!fs.existsSync(filePath)) {
    const emptyConfig: ConfigInfo = {};
    configCache.set(filePath, emptyConfig);
    return emptyConfig;
  }

  const raw = fs.readFileSync(filePath, 'utf-8');
  const errors: jsonc.ParseError[] = [];
  const parsed = jsonc.parse(raw, errors, { allowTrailingComma: true });

  if (errors.length > 0) {
    const error = errors[0];
    const lineNum = raw.substring(0, error.offset).split('\n').length;
    throw new Error(
      `Failed to parse config file ${filePath}: ${jsonc.printParseErrorCode(error.error)} at line ${lineNum}`
    );
  }

  const result = ConfigInfoSchema.safeParse(parsed);
  if (!result.success) {
    const issues = result.error.issues
      .map((i) => `  ${i.path.join('.')}: ${i.message}`)
      .join('\n');
    throw new Error(`Invalid config in ${filePath}:\n${issues}`);
  }

  if (result.data._version === undefined) {
    result.data._version = Date.now();
  }

  configCache.set(filePath, result.data);
  return result.data;
}

export function getProviderConfig(
  config: ConfigInfo,
  providerID: string
): ResolvedProvider {
  const builtin = BUILTIN_PROVIDERS[providerID] as BuiltinProvider | undefined;
  const fileProvider = config.provider?.[providerID];

  let apiKey: string | null = null;
  let apiKeySource: ResolvedProvider['source'] = 'none';

  if (fileProvider?.apiKey) {
    apiKey = fileProvider.apiKey;
    apiKeySource = 'config';
  } else if (builtin?.envVars) {
    for (const envVar of builtin.envVars) {
      const val = process.env[envVar];
      if (val) {
        apiKey = val;
        apiKeySource = 'env';
        break;
      }
    }
  }

  let baseURL: string | null = null;
  if (fileProvider?.baseURL) {
    baseURL = fileProvider.baseURL;
  } else if (builtin?.baseURL) {
    baseURL = builtin.baseURL;
  }

  const sdk = fileProvider?.sdk ?? builtin?.sdk ?? 'openai-compatible';
  const name = fileProvider?.name ?? builtin?.name ?? providerID;
  const envVars = builtin?.envVars ?? [];

  return { sdk, name, apiKey, baseURL, envVars, source: apiKeySource };
}

export interface WriteConfigOptions {
  expectedVersion?: number;
}

export function writeConfig(
  config: ConfigInfo,
  configPath?: string,
  options?: WriteConfigOptions
): void {
  const filePath = configPath ?? CONFIG_PATH();

  const currentConfig = load(filePath);
  if (options?.expectedVersion !== undefined) {
    if (currentConfig._version !== undefined && currentConfig._version !== options.expectedVersion) {
      throw new ConfigConflictError(currentConfig._version, options.expectedVersion);
    }
  }

  ensureConfigDir();

  clearConfigCache();

  config._version = Math.max(Date.now(), (currentConfig._version ?? 0) + 1);

  const content = JSON.stringify(config, null, 2) + '\n';
  const tmpPath = filePath + '.tmp.' + process.pid;

  fs.writeFileSync(tmpPath, content, { mode: 0o600 });
  fs.renameSync(tmpPath, filePath);
}

export function getConfigPath(): string {
  return CONFIG_PATH();
}

export function getConfigDir(): string {
  return CONFIG_DIR();
}

export interface ServerConfig {
  port: number;
  host: string;
}

export function getServerConfig(config: ConfigInfo): ServerConfig {
  return {
    port: config.server?.port ?? 3456,
    host: config.server?.host ?? '127.0.0.1',
  };
}

export interface LogConfig {
  level: 'DEBUG' | 'INFO' | 'WARN' | 'ERROR';
}

export function getLogConfig(config: ConfigInfo): LogConfig {
  return {
    level: config.log?.level ?? 'INFO',
  };
}

export interface AgentTimeoutConfig {
  taskTimeout: number;
  stageTimeout: number;
  maxGracePeriods: number;
}

export function getAgentTimeoutConfig(config: ConfigInfo): AgentTimeoutConfig {
  return {
    taskTimeout: config.agent?.taskTimeout ?? 600,
    stageTimeout: config.agent?.stageTimeout ?? 3600,
    maxGracePeriods: config.agent?.maxGracePeriods ?? 2,
  };
}

export function resolveOpencodeBinPath(config?: ConfigInfo): string | undefined {
  const envPath = process.env['OPENCODE_BIN_PATH'];
  if (envPath) {
    return envPath;
  }

  if (config?.opencode?.binPath) {
    return config.opencode.binPath;
  }

  const homePath = path.join(os.homedir(), '.opencode', 'bin', 'opencode');
  if (fs.existsSync(homePath)) {
    return homePath;
  }

  try {
    const whichResult = execFileSync('which', ['opencode'], { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'pipe'] }).trim();
    if (whichResult) {
      return whichResult;
    }
  } catch {
    // which command failed, opencode not in PATH
  }

  return undefined;
}
