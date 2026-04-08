# crawlph

AI-powered GitHub workflow automation tool that manages Issues through a structured workflow using AI agents.

## Features

- **Structured Workflow**: draft → plan → build → check → done (with optional review stage)
- **AI Agents**: Automatic design and implementation using opencode agents
- **User Checkpoints**: Review and approve designs and implementations before proceeding
- **Single PR Mode**: Each issue has one PR that accumulates both design and implementation
- **Concurrent Processing**: Handle up to 8 issues simultaneously
- **State Persistence**: Resume from where you left off after restart

## Installation

```bash
npm install -g crawlph-cli
```

## Quick Start

### 1. Start the Server

```bash
crawlph server start
```

The server runs on `localhost:3456` by default.

### 2. Create a Project

```bash
crawlph project create my-project --repo owner/repo
```

### 3. Process Issues

```bash
# List issues
crawlph issue list

# Start processing an issue
crawlph issue start 42

# Check status
crawlph issue show 42
```

## Usage

### Server Management

```bash
# Start server in daemon mode
crawlph server start

# Check server status
crawlph server status

# View server logs
crawlph server logs

# Stop server
crawlph server stop
```

### Project Management

```bash
# Create a project
crawlph project create <name> --repo <owner/repo>

# List all projects
crawlph project list

# Switch to a project
crawlph project use <name>

# Show project details
crawlph project show <name>

# Remove a project
crawlph project remove <name>
```

### Issue Management

```bash
# List issues (optionally filter by stage)
crawlph issue list
crawlph issue list --status designing

# Show issue details
crawlph issue show <number>

# Start processing an issue
crawlph issue start <number>

# Pause processing
crawlph issue pause <number>

# Resume processing
crawlph issue resume <number>
```

### Pull Request Management

```bash
# List PRs
crawlph pr list

# Show PR details
crawlph pr show <number>

# Open PR in browser
crawlph pr review <number>

# Approve PR
crawlph pr approve <number>
crawlph pr approve <number> --message "Looks good!"

# Request changes
crawlph pr request-changes <number> "Please fix the tests"
```

### Quick Commands

```bash
# Show current project status
crawlph status

# Show all projects status
crawlph status --all

# Get configuration
crawlph config --list

# Set configuration
crawlph config pollInterval 60000
```

## Workflow

The workflow has 5 stages (with an optional review stage):

1. **draft**: Initial state for new issues
2. **plan**: AI agent explores the issue and creates design/plan
3. **review**: (Optional) Human review and approval of plan artifacts
4. **build**: AI agent implements the plan
5. **check**: Run tests, verify implementation, and archive
6. **done**: Issue completed

### User Checkpoints

At critical points, the workflow pauses for user review:

- **Review Stage** (optional): After plan stage, review the Change artifacts and approve with `mo issue approve`
- **Check Stage**: After build stage, review the code and approve with `mo issue approve`

## OpenSpec Workflow

For complex issues, Mohist supports the **OpenSpec workflow** with structured task decomposition and Ralph-style execution:

```
plan → review → build → check → done
```

### Key Features

- **Change Artifacts**: Structured proposal/design/specs stored in `.mohist-specs/changes/`
- **Ralph Loop**: Task-by-task execution with full context and failure recovery
- **Self-Review**: Agent validates specs before human review (up to 3 iterations)
- **Session Memory**: Learnings passed between tasks

### Quick Start

```bash
# Start server
mo server start

# Create Change for issue #42 (starts plan stage)
mo propose 42

# After review, approve to start build
mo issue approve 42

# Build executes tasks automatically
# When done, approve to archive
mo issue approve 42
```

### Commands

| Command | Description |
|---------|-------------|
| `mo propose <issue>` | Create Change and start plan |
| `mo propose <issue> --force` | Overwrite existing Change |
| `mo issue resume <id> --skip-to-review` | Resume after manual fixes |

### Documentation

- [OpenSpec Usage Guide](OPENSPEC-USAGE.md) - Detailed usage
- [Workflow Examples](workflow-example/) - Configuration templates
- [Troubleshooting](TROUBLESHOOTING.md) - Common issues and solutions

## Configuration

Configuration is stored in `~/.crawlph/config.json`:

```json
{
  "githubToken": "your-token-here",
  "serverPort": 3456,
  "pollInterval": 60000,
  "maxConcurrentAgents": 8,
  "agentTimeout": 1800000
}
```

## Architecture

crawlph uses a **fat server, thin client** architecture:

- **Server**: Handles all business logic, agent execution, and state management
- **CLI**: Pretty interface that communicates with server via HTTP API
- **Agents**: Independent opencode processes spawned by the server

## Development

```bash
# Install dependencies
npm install

# Build
npm run build

# Run tests
npm test

# Run tests with coverage
npm run test:coverage

# Lint
npm run lint

# Type check
npm run typecheck
```

## Requirements

- Node.js >= 18.0.0
- GitHub token with repo permissions
- opencode CLI installed and configured

## License

MIT

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development guidelines.
