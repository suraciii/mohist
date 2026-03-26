# Cross-Platform Compatibility Testing

This document describes how to test crawlph on different operating systems.

## Supported Platforms

- **macOS**: 10.15 (Catalina) and later
- **Linux**: Major distributions (Ubuntu, Debian, Fedora, CentOS)
- **Windows**: Windows 10 and later (with WSL 2 recommended)

## Prerequisites

### All Platforms

- Node.js >= 18.0.0
- npm >= 9.0.0
- GitHub account with repository access
- opencode CLI installed

### Platform-Specific

#### macOS

- Xcode Command Line Tools: `xcode-select --install`

#### Linux

- Build tools: `sudo apt-get install build-essential` (Ubuntu/Debian)
- Python 3 (for node-gyp)

#### Windows

- Windows Build Tools: `npm install -g windows-build-tools`
- Or use WSL 2 (recommended)

## Testing Checklist

### 1. Installation

```bash
# Install from npm
npm install -g crawlph-cli

# Verify installation
crawlph --version
crawlph-server --version
```

**Platforms**: ✅ macOS | ✅ Linux | ✅ Windows

### 2. Server Management

```bash
# Start server
crawlph server start

# Check status
crawlph server status

# View logs
crawlph server logs

# Stop server
crawlph server stop
```

**Platforms**: ✅ macOS | ✅ Linux | ✅ Windows

**Known Issues**:
- Windows: May require WSL for daemon mode
- Windows: Process management may differ

### 3. Project Management

```bash
# Create project
crawlph project create test --repo owner/repo

# List projects
crawlph project list

# Switch project
crawlph project use test

# Remove project
crawlph project remove test
```

**Platforms**: ✅ macOS | ✅ Linux | ✅ Windows

### 4. Issue Management

```bash
# List issues
crawlph issue list

# Show issue
crawlph issue show 1

# Start processing
crawlph issue start 1

# Pause/Resume
crawlph issue pause 1
crawlph issue resume 1
```

**Platforms**: ✅ macOS | ✅ Linux | ✅ Windows

### 5. PR Management

```bash
# List PRs
crawlph pr list

# Show PR
crawlph pr show 1

# Open in browser
crawlph pr review 1

# Approve
crawlph pr approve 1

# Request changes
crawlph pr request-changes 1 "Please fix"
```

**Platforms**: ✅ macOS | ✅ Linux | ⚠️ Windows

**Known Issues**:
- Windows: Browser opening may use different command

### 6. Configuration

```bash
# List config
crawlph config --list

# Set config
crawlph config pollInterval 60000
```

**Platforms**: ✅ macOS | ✅ Linux | ✅ Windows

### 7. File Paths

Test path handling across platforms:

- **Home directory**: Should resolve `~` correctly
- **Config path**: `~/.crawlph/`
- **Log path**: `~/.crawlph/logs/`

**Platforms**: ✅ macOS | ✅ Linux | ⚠️ Windows

**Known Issues**:
- Windows: Use `%USERPROFILE%` instead of `~`

### 8. Process Management

Test daemon mode and process handling:

```bash
# Start daemon
crawlph server start

# Find process
# macOS/Linux: ps aux | grep crawlph
# Windows: tasklist | findstr node

# Stop daemon
crawlph server stop
```

**Platforms**: ✅ macOS | ✅ Linux | ⚠️ Windows

**Known Issues**:
- Windows: May need different process management approach

### 9. Agent Execution

Test spawning opencode agents:

```bash
# Start an issue
crawlph issue start 1

# Check logs for agent execution
crawlph server logs
```

**Platforms**: ✅ macOS | ✅ Linux | ✅ Windows (WSL)

### 10. End-to-End Flow

Complete workflow test:

```bash
# Setup
crawlph server start
crawlph project create test --repo owner/repo

# Process issue
crawlph issue start 1

# Wait for design review
crawlph issue show 1
crawlph pr approve 1

# Wait for implementation review
crawlph pr approve 1

# Verify completion
crawlph issue show 1

# Cleanup
crawlph project remove test
crawlph server stop
```

**Platforms**: ✅ macOS | ✅ Linux | ✅ Windows (WSL)

## Platform-Specific Notes

### macOS

- ✅ Full compatibility
- Uses `open` command for browser
- Uses `ps` for process management

### Linux

- ✅ Full compatibility
- Uses `xdg-open` for browser
- Uses `ps` for process management

### Windows

- ⚠️ Partial compatibility
- **Recommended**: Use WSL 2 for best results
- Uses `start` command for browser
- Process management may differ

#### Windows Native (No WSL)

Issues:
1. Daemon mode may not work as expected
2. Shell commands differ from Unix
3. Path separators differ (`\` vs `/`)

Workarounds:
```bash
# Use PowerShell instead of CMD
# Run server in foreground (not daemon)
node bin/crawlph-server
```

#### Windows with WSL 2

- ✅ Full compatibility
- Follow Linux instructions
- Install Node.js in WSL

## Testing Automation

### GitHub Actions

Test on multiple platforms using GitHub Actions:

```yaml
name: Cross-Platform Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ${{ matrix.os }}
    strategy:
      matrix:
        os: [macos-latest, ubuntu-latest, windows-latest]
        node: [18, 20]

    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-node@v3
        with:
          node-version: ${{ matrix.node }}
      - run: npm install
      - run: npm run build
      - run: npm test
```

## Reporting Issues

When reporting platform-specific issues, include:

1. Operating system and version
2. Node.js version (`node --version`)
3. npm version (`npm --version`)
4. Error message and stack trace
5. Steps to reproduce

## Known Limitations

### Windows Native

- Daemon mode may require different implementation
- Some shell commands may not work
- Path handling may differ

### WSL 1

- File system performance may be slower
- Some system calls may differ

### All Platforms

- GitHub API rate limits apply universally
- Network connectivity required for all operations

## Future Improvements

1. Add native Windows service support
2. Improve error messages for platform-specific issues
3. Add platform detection and warnings
4. Create platform-specific installation guides
