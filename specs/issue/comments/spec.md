# Comments

Comments add discussion to an [Issue](../lifecycle/spec.md).
[Comment mentions](design.md) define how an Agent responds to a named request.

## Comments

```bash
# Add a comment. The authenticated identity is the author.
# --display-name is only a display alias.
mo issue comment create 42 --display-name "Ada" --body "Looks good but check edge cases"

# The CLI does not currently delete comments. Use the Web UI or API.
```

The comment area is at the bottom of the Issue details page. During Plan, the
stage's Mohist Agent reads comments as additional context.
