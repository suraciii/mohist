# Contributing to crawlph

Thank you for your interest in contributing to crawlph!

## Development Setup

### Prerequisites

- Node.js >= 18.0.0
- npm >= 9.0.0
- GitHub account with repository access
- opencode CLI installed

### Installation

```bash
# Clone the repository
git clone https://github.com/owner/crawlph.git
cd crawlph

# Install dependencies
npm install

# Build the project
npm run build
```

### Development Workflow

1. **Create a feature branch**
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Make your changes**
   - Follow the existing code style
   - Add tests for new functionality
   - Update documentation as needed

3. **Run tests**
   ```bash
   npm test
   ```

4. **Run linter**
   ```bash
   npm run lint
   ```

5. **Type check**
   ```bash
   npm run typecheck
   ```

6. **Commit your changes**
   ```bash
   git add .
   git commit -m "feat: add your feature"
   ```

7. **Push and create PR**
   ```bash
   git push origin feature/your-feature-name
   ```

## Project Structure

```
crawlph-cli/
├── bin/                    # CLI entry points
│   ├── crawlph            # Main CLI
│   └── crawlph-server     # Server entry point
├── src/
│   ├── agent/             # Agent runner and prompts
│   ├── api/               # HTTP API routes
│   ├── cli/               # CLI commands
│   ├── github/            # GitHub API client
│   ├── poller/            # Status poller
│   ├── project/           # Project management
│   ├── server/            # HTTP server
│   ├── types/             # TypeScript types
│   └── workflow/          # Issue workflow logic
├── tests/                 # Test files
├── package.json
├── tsconfig.json
└── vitest.config.ts
```

## Code Style

### TypeScript

- Use strict mode
- Prefer interfaces over types for object shapes
- Use enums for fixed sets of values
- Add JSDoc comments for public APIs

### General

- Use meaningful variable names
- Keep functions small and focused
- Prefer composition over inheritance
- Write self-documenting code

## Testing

### Unit Tests

Place unit tests next to the source files or in the `tests/` directory:

```typescript
import { describe, it, expect } from 'vitest';

describe('MyComponent', () => {
  it('should do something', () => {
    // test code
  });
});
```

### Integration Tests

Integration tests should be in `tests/` with the suffix `.integration.test.ts`:

```bash
# Run integration tests
npm run test:integration
```

### E2E Tests

Follow the guide in `tests/E2E-TEST-GUIDE.md` for manual E2E testing.

## Commit Messages

Follow the [Conventional Commits](https://www.conventionalcommits.org/) specification:

- `feat:` New features
- `fix:` Bug fixes
- `docs:` Documentation changes
- `style:` Code style changes (formatting, etc.)
- `refactor:` Code refactoring
- `test:` Test additions/changes
- `chore:` Build process or auxiliary tool changes

Example:
```
feat: add pause/resume functionality for issues

- Implement pause command
- Implement resume command
- Add tests for pause/resume
- Update README with examples
```

## Pull Request Guidelines

1. **Title**: Use conventional commit format
2. **Description**: Explain what and why, not how
3. **Tests**: Include tests for new functionality
4. **Documentation**: Update relevant documentation
5. **Breaking Changes**: Clearly mark any breaking changes

## Architecture Decisions

When making significant architectural changes:

1. **Document the decision** in `docs/architecture/`
2. **Explain the rationale** (why, not just what)
3. **Consider alternatives** and why they were rejected
4. **Think about trade-offs** and future implications

## Debugging

### Server Logs

```bash
# View server logs
crawlph server logs

# View last 100 lines
crawlph server logs -n 100
```

### Agent Logs

Agent output is captured in the server logs.

### Debug Mode

Set the `DEBUG` environment variable:

```bash
DEBUG=crawlph:* crawlph server start
```

## Release Process

1. Update version in `package.json`
2. Update `CHANGELOG.md`
3. Create git tag: `git tag v1.0.0`
4. Push tag: `git push origin v1.0.0`
5. CI/CD will handle publishing

## Getting Help

- **Issues**: Open an issue on GitHub
- **Discussions**: Use GitHub Discussions
- **Documentation**: Check the README and inline docs

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
