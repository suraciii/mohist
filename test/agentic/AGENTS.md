# Agentic Testing Context

## Structure

```
test/agentic/
├── shared/              # Shared infrastructure
│   ├── Containerfile    # Base container (no opencode)
│   └── entrypoint.sh    # Starts mo-server
└── verify-<feature>/
    ├── TESTPLAN.md      # Natural language test plan
    ├── test.sh          # Deterministic execution
    └── run.sh           # podman build + run
```

## Running

```bash
cd test/agentic/verify-<feature> && bash run.sh
```

## Container

- User: `motest`
- Workspace: `/app/workspace/`
- Data: `/home/motest/.mohist/`
- mohist source: `/opt/mohist-src` (built)
- Server: `localhost:3456` (started by entrypoint)
- No opencode (Layer A doesn't need it)
