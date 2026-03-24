import { HttpServer } from './http-server';
import { Config } from '../types';
import * as fs from 'fs';
import * as path from 'path';

const DEFAULT_CONFIG: Config = {
  serverPort: 3456,
  pollInterval: 60000,
  maxConcurrentAgents: 8,
  agentTimeout: 1800000
};

function loadConfig(): Config {
  const configPath = path.join(process.env.HOME || '', '.crawlph', 'config.json');
  
  if (fs.existsSync(configPath)) {
    const configData = fs.readFileSync(configPath, 'utf-8');
    return { ...DEFAULT_CONFIG, ...JSON.parse(configData) };
  }
  
  return DEFAULT_CONFIG;
}

function ensureDataDir(): void {
  const dataDir = path.join(process.env.HOME || '', '.crawlph');
  
  if (!fs.existsSync(dataDir)) {
    fs.mkdirSync(dataDir, { recursive: true });
  }
  
  const logsDir = path.join(dataDir, 'logs');
  if (!fs.existsSync(logsDir)) {
    fs.mkdirSync(logsDir, { recursive: true });
  }
}

async function main(): Promise<void> {
  ensureDataDir();
  
  const config = loadConfig();
  const server = new HttpServer(config);

  process.on('SIGTERM', async () => {
    console.log('Received SIGTERM, shutting down gracefully...');
    await server.stop();
    process.exit(0);
  });

  process.on('SIGINT', async () => {
    console.log('Received SIGINT, shutting down gracefully...');
    await server.stop();
    process.exit(0);
  });

  await server.start();
  
  console.log(`crawlph server started on port ${config.serverPort}`);
  console.log(`Max concurrent agents: ${config.maxConcurrentAgents}`);
}

main().catch((error) => {
  console.error('Failed to start server:', error);
  process.exit(1);
});
