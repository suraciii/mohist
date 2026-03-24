# crawlph

AI-powered GitHub workflow automation tool that manages Issues through a 7-stage workflow using AI agents.

## Features

- **7-Stage Workflow**: draft → designing → waiting-design-review → implementing → waiting-review → merging → done
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

The workflow has 7 stages:

1. **draft**: Initial state for new issues
2. **designing**: AI agent creates a design document
3. **waiting-design-review**: Waiting for user to review and approve the design
4. **implementing**: AI agent implements the design
5. **waiting-review**: Waiting for user to review and approve the implementation
6. **merging**: PR is being merged
7. **done**: Issue completed and PR merged

### User Checkpoints

At two critical points, the workflow pauses for user review:

- **Design Review**: After designing stage, review the design document and approve with `crawlph pr approve`
- **Implementation Review**: After implementing stage, review the code and approve with `crawlph pr approve`

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

## GitHub Labels

crawlph uses GitHub Labels to track issue state:

- `crawlph:stage/draft`
- `crawlph:stage/designing`
- `crawlph:stage/waiting-design-review`
- `crawlph:stage/implementing`
- `crawlph:stage/waiting-review`
- `crawlph:stage/merging`
- `crawlph:stage/done`

And status labels:

- `crawlph:status/active`
- `crawlph:status/paused`
- `crawlph:status/blocked`

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
