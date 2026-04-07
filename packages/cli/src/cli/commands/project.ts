import { Command } from 'commander';
import chalk from 'chalk';
import * as path from 'path';
import { ApiResponse, Project } from '../../types';
import { apiClient } from '../api-client';
import { requireServer } from '../server-check';

export function setupProjectCommands(program: Command): void {
  const project = program.command('project').description('Manage projects');

  project.hook('preAction', async () => {
    await requireServer();
  });

  project
    .command('create <name>')
    .description('Create a new project')
    .option('--path <path>', 'Project path (default: current directory)')
    .action(async (name, options) => {
      try {
        const projectPath = options.path || process.cwd();
        
        const response = await apiClient<ApiResponse<Project>>(
          'POST',
          '/projects',
          { name, path: projectPath }
        );
        
        if (response.success && response.data) {
          console.log(chalk.green(`✓ Project created: ${response.data.name}`));
          console.log(chalk.gray(`  Path: ${response.data.path}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to create project: ${error}`));
      }
    });

  project
    .command('list')
    .description('List all projects')
    .action(async () => {
      try {
        const response = await apiClient<ApiResponse<Project[]>>('GET', '/projects');
        
        if (response.success && response.data) {
          if (response.data.length === 0) {
            console.log(chalk.yellow('No projects found'));
            return;
          }
          
          console.log(chalk.bold('\nProjects:\n'));
          response.data.forEach((p, i) => {
            console.log(`  ${i + 1}. ${chalk.cyan(p.name)} - ${chalk.gray(p.path)}`);
          });
          console.log();
        }
      } catch (error) {
        console.error(chalk.red(`Failed to list projects: ${error}`));
      }
    });

  project
    .command('use <name>')
    .description('Switch to a project')
    .action(async (name) => {
      try {
        const response = await apiClient<ApiResponse<Project>>(
          'POST',
          `/projects/${name}/use`
        );
        
        if (response.success && response.data) {
          console.log(chalk.green(`✓ Switched to project: ${response.data.name}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to switch project: ${error}`));
      }
    });

  project
    .command('remove <name>')
    .description('Remove a project')
    .action(async (name) => {
      try {
        const response = await apiClient<ApiResponse>(
          'DELETE',
          `/projects/${name}`
        );
        
        if (response.success) {
          console.log(chalk.green(`✓ Project removed: ${name}`));
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to remove project: ${error}`));
      }
    });

  project
    .command('show <name>')
    .description('Show project details')
    .action(async (name) => {
      try {
        const response = await apiClient<ApiResponse<Project>>(
          'GET',
          `/projects/${name}`
        );
        
        if (response.success && response.data) {
          const p = response.data;
          console.log(chalk.bold(`\nProject: ${p.name}\n`));
          console.log(`  Path: ${chalk.gray(p.path)}`);
          console.log(`  Created: ${chalk.gray(p.createdAt)}`);
          console.log();
        } else {
          console.error(chalk.red(`Error: ${response.error}`));
        }
      } catch (error) {
        console.error(chalk.red(`Failed to show project: ${error}`));
      }
    });
}

export function setupInitCommand(program: Command): void {
  program
    .command('init [name]')
    .description('Initialize a project in the current directory')
    .action(async (name) => {
      try {
        const currentDir = process.cwd();
        const projectName = name || path.basename(currentDir);
        
        const response = await apiClient<ApiResponse<Project>>(
          'POST',
          '/projects',
          { name: projectName, path: currentDir }
        );
        
        if (response.success && response.data) {
          console.log(chalk.green(`✓ Initialized project: ${response.data.name}`));
          console.log(chalk.gray(`  Path: ${response.data.path}`));
          
          const useResponse = await apiClient<ApiResponse>(
            'POST',
            `/projects/${projectName}/use`
          );
          
          if (useResponse.success) {
            console.log(chalk.gray(`  Set as current project`));
          }
        } else {
          if (response.error?.includes('already exists')) {
            console.log(chalk.yellow(`Project "${projectName}" already exists`));
            
            const useResponse = await apiClient<ApiResponse>(
              'POST',
              `/projects/${projectName}/use`
            );
            
            if (useResponse.success) {
              console.log(chalk.green(`✓ Switched to project: ${projectName}`));
            }
          } else {
            console.error(chalk.red(`Error: ${response.error}`));
          }
        }
      } catch (error) {
        console.error(chalk.red(`Failed to initialize project: ${error}`));
      }
    });
}
