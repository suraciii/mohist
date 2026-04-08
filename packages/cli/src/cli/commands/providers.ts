import { Command } from 'commander';
import chalk from 'chalk';
import * as readline from 'node:readline';
import {
  load,
  writeConfig,
  getProviderConfig,
  getConfigPath,
} from '../../config/config-loader';
import { BUILTIN_PROVIDERS } from '../../config/builtin-providers';
import type { ConfigInfo } from '../../config/config-schema';

function maskApiKey(apiKey: string): string {
  if (apiKey.length <= 6) {
    return '***';
  }
  const prefix = apiKey.substring(0, 3);
  const suffix = apiKey.substring(apiKey.length - 3);
  return `${prefix}***...${suffix}`;
}

function promptInput(question: string, hidden = false): Promise<string> {
  return new Promise((resolve) => {
    const rl = readline.createInterface({
      input: process.stdin,
      output: process.stdout,
    });

    if (hidden) {
      const stdin = process.stdin;
      const onData = (char: Buffer) => {
        const s = char.toString();
        if (s === '\n' || s === '\r' || s === '\u0004') {
          stdin.removeListener('data', onData);
        } else if (s === '\u0003') {
          process.exit();
        } else {
          process.stdout.clearLine(0);
          process.stdout.cursorTo(0);
          process.stdout.write(question);
        }
      };

      rl.question(question, (answer) => {
        console.log();
        rl.close();
        resolve(answer.trim());
      });

      if (stdin.isTTY) {
        stdin.on('data', onData);
      }
    } else {
      rl.question(question, (answer) => {
        rl.close();
        resolve(answer.trim());
      });
    }
  });
}

async function listProviders(): Promise<void> {
  const config = load();
  const providerIDs = Object.keys(BUILTIN_PROVIDERS);

  console.log(chalk.bold('\nProviders:\n'));
  console.log('  ID            Name                Status              API Key              Base URL');
  console.log('  ' + '─'.repeat(95));

  for (const id of providerIDs) {
    const resolved = getProviderConfig(config, id);
    const idCol = chalk.cyan(id.padEnd(13));
    const nameCol = resolved.name.padEnd(19);

    let statusCol: string;
    let apiKeyCol: string;

    if (resolved.source === 'config') {
      statusCol = chalk.green('configured (cfg)').padEnd(19);
      apiKeyCol = maskApiKey(resolved.apiKey!).padEnd(19);
    } else if (resolved.source === 'env') {
      statusCol = chalk.green('configured (env)').padEnd(19);
      apiKeyCol = maskApiKey(resolved.apiKey!).padEnd(19);
    } else {
      statusCol = chalk.gray('not configured').padEnd(19);
      apiKeyCol = chalk.gray('—').padEnd(19);
    }

    const baseURLCol = resolved.baseURL
      ? chalk.gray(resolved.baseURL)
      : chalk.gray('—');

    console.log(`  ${idCol} ${nameCol} ${statusCol} ${apiKeyCol} ${baseURLCol}`);
  }
  console.log();
}

async function loginProvider(providerID: string): Promise<void> {
  const builtin = BUILTIN_PROVIDERS[providerID];
  const config = load();
  const current = getProviderConfig(config, providerID);

  if (builtin) {
    const label = builtin.baseURL
      ? `${builtin.name} (${builtin.baseURL})`
      : builtin.name;
    console.log(chalk.bold(label));

    const currentHint =
      current.apiKey && current.source === 'config'
        ? ` (currently ${maskApiKey(current.apiKey)})`
        : '';
    const apiKey = await promptInput(`API Key${currentHint}: `, true);

    if (!apiKey) {
      console.error(chalk.red('Error: API Key cannot be empty'));
      process.exit(1);
    }

    const updated: ConfigInfo = {
      ...config,
      provider: {
        ...config.provider,
        [providerID]: {
          ...config.provider?.[providerID],
          apiKey,
        },
      },
    };

    writeConfig(updated);
    console.log(chalk.green(`✓ Saved to ${getConfigPath()}`));
  } else {
    console.log(
      chalk.yellow(
        `Provider '${providerID}' is not a built-in provider.`
      )
    );

    const apiKey = await promptInput('API Key: ', true);
    if (!apiKey) {
      console.error(chalk.red('Error: API Key cannot be empty'));
      process.exit(1);
    }

    const baseURL = await promptInput('Base URL: ');
    if (!baseURL) {
      console.error(chalk.red('Error: Base URL is required for custom providers'));
      process.exit(1);
    }

    const updated: ConfigInfo = {
      ...config,
      provider: {
        ...config.provider,
        [providerID]: {
          apiKey,
          baseURL,
          sdk: 'openai-compatible',
        },
      },
    };

    writeConfig(updated);
    console.log(chalk.green(`✓ Saved to ${getConfigPath()}`));
  }
}

async function logoutProvider(providerID: string): Promise<void> {
  const config = load();
  const providerSection = config.provider?.[providerID];

  if (!providerSection?.apiKey) {
    console.log(chalk.yellow(`Provider '${providerID}' is not configured`));
    return;
  }

  const { apiKey: _apiKey, ...rest } = providerSection;
  const hasOtherFields = Object.keys(rest).length > 0;

  const updatedProvider = { ...config.provider };

  if (hasOtherFields) {
    updatedProvider[providerID] = rest;
  } else {
    delete updatedProvider[providerID];
  }

  const updated: ConfigInfo = {
    ...config,
    provider:
      Object.keys(updatedProvider).length > 0 ? updatedProvider : undefined,
  };

  writeConfig(updated);
  console.log(
    chalk.green(`✓ Removed ${providerID} credentials from ${getConfigPath()}`)
  );
}

export function setupProvidersCommands(program: Command): void {
  const providers = program
    .command('providers')
    .description('Manage LLM providers');

  providers
    .command('list')
    .alias('ls')
    .description('List all provider statuses')
    .action(async () => {
      await listProviders();
    });

  providers
    .command('login <providerID>')
    .description('Configure API key for a provider')
    .action(async (providerID: string) => {
      await loginProvider(providerID);
    });

  providers
    .command('logout <providerID>')
    .description('Remove API key for a provider')
    .action(async (providerID: string) => {
      await logoutProvider(providerID);
    });
}
