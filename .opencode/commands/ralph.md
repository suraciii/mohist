---
description: Run openspec ralph iteration with environment setup
---

Run openspec ralph iteration for the specified change.

First, execute the environment setup command:

```bash
unset OPENCODE_SERVER_USERNAME OPENCODE_SERVER_PASSWORD && export PATH="/home/surac/.opencode/bin:$PATH" && openspec ralph --change "{{input}}"
```

If the command fails with environment issues, ensure the PATH is set correctly and try again.

Then, load and follow the opsx-ralph skill instructions to complete one Ralph iteration for change: {{input}}
