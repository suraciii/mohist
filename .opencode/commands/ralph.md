---
description: Run openspec ralph iteration with environment setup
---

Run openspec ralph iteration.

## Determine the change name

If `{{input}}` is provided, use it directly as the change name.

If `{{input}}` is not provided or empty, auto-detect the active change:

1. Run `openspec list` to list all changes
2. If there is exactly one non-archived change, use its name
3. If there are multiple non-archived changes, ask the user which one to work on
4. If there are no changes, tell the user and stop

## Execute

Once the change name is determined (referred to as CHANGE_NAME below), execute:

```bash
unset OPENCODE_SERVER_USERNAME OPENCODE_SERVER_PASSWORD && export PATH="/home/surac/.opencode/bin:$PATH" && openspec ralph --change "CHANGE_NAME"
```

If the command fails with environment issues, ensure the PATH is set correctly and try again.

Then, load and follow the opsx-ralph skill instructions to complete one Ralph iteration for change: CHANGE_NAME
