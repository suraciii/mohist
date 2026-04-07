import chalk from 'chalk';
import http from 'http';

function checkServerHealth(): Promise<boolean> {
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

export async function requireServer(): Promise<void> {
  const isRunning = await checkServerHealth();
  if (!isRunning) {
    console.error(chalk.red('Error: Server is not running'));
    console.error(chalk.yellow('Start the server with: mo server start'));
    process.exit(1);
  }
}

export function formatError(error: string): void {
  console.error(chalk.red(`Error: ${error}`));
}
