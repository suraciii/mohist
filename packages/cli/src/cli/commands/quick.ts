import { Command } from 'commander';
import chalk from 'chalk';
import http from 'http';
import { ApiResponse } from '../../types';

const API_BASE = 'http://localhost:3456/api';

function apiClient<T = any>(
  method: string,
  path: string,
  body?: any
): Promise<T> {
  return new Promise((resolve, reject) => {
    const data = body ? JSON.stringify(body) : undefined;
    
    const req = http.request(
      `${API_BASE}${path}`,
      {
        method,
        headers: {
          'Content-Type': 'application/json',
          'Content-Length': data ? Buffer.byteLength(data) : 0
        }
      },
      (res) => {
        let responseData = '';
        
        res.on('data', (chunk) => {
          responseData += chunk;
        });
        
        res.on('end', () => {
          try {
            const parsed = JSON.parse(responseData);
            resolve(parsed);
          } catch (error) {
            reject(new Error('Invalid JSON response'));
          }
        });
      }
    );
    
    req.on('error', reject);
    
    if (data) {
      req.write(data);
    }
    
    req.end();
  });
}

export function setupQuickCommands(program: Command): void {
  program
    .command('status')
    .description('Show current project status')
    .option('--all', 'Show all projects status')
    .action(async (options) => {
      try {
        const path = options.all ? '/status?all=true' : '/status';
        const response = await apiClient<ApiResponse>('GET', path);
        
        if (response.success && response.data) {
          if (options.all) {
            console.log(chalk.bold('\nAll Projects Status:\n'));
          } else {
            console.log(chalk.bold('\nCurrent Project Status:\n'));
          }
          
          console.log(JSON.stringify(response.data, null, 2));
          console.log();
        }
      } catch (error) {
        console.error(chalk.red(`Failed to get status: ${error}`));
      }
    });

  program
    .command('config')
    .description('Manage configuration')
    .argument('[key]', 'Config key')
    .argument('[value]', 'Config value')
    .option('-l, --list', 'List all config')
    .action(async (key, value, options) => {
      try {
        if (options.list) {
          const response = await apiClient<ApiResponse<{ [key: string]: any }>>(
            'GET',
            '/config'
          );
          
          if (response.success && response.data) {
            console.log(chalk.bold('\nConfiguration:\n'));
            Object.entries(response.data).forEach(([k, v]) => {
              console.log(`  ${chalk.cyan(k)}: ${v}`);
            });
            console.log();
          }
        } else if (key && value) {
          const response = await apiClient<ApiResponse>(
            'PUT',
            `/config/${key}`,
            { value }
          );
          
          if (response.success) {
            console.log(chalk.green(`✓ Set ${key} = ${value}`));
          } else {
            console.error(chalk.red(`Error: ${response.error}`));
          }
        } else if (key) {
          const response = await apiClient<ApiResponse<{ [key: string]: any }>>(
            'GET',
            '/config'
          );
          
          if (response.success && response.data) {
            const v = response.data[key];
            if (v !== undefined) {
              console.log(v);
            } else {
              console.error(chalk.red(`Config key not found: ${key}`));
            }
          }
        } else {
          console.log(chalk.yellow('Usage:'));
          console.log('  crawlph config --list');
          console.log('  crawlph config <key>');
          console.log('  crawlph config <key> <value>');
        }
      } catch (error) {
        console.error(chalk.red(`Failed to manage config: ${error}`));
      }
    });
}
