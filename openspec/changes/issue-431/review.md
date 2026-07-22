# Review Findings

## F-01 (MEDIUM): Extract preview still reports escaped references as variables

**Location:** `packages/server/src/Mohist.Server/Workflow/Services/Prompts/PromptTemplateEngine.cs:85-95`, exposed by `POST /api/templates/extract-variables` in `TemplateRoutes.cs`.

`PromptTemplateEngine.Render` correctly consumes `\${{` as a literal opening sequence, but `ExtractVariables` scans the original body directly with `TokenRegex.Matches(body)`. Therefore an escaped example such as `use \${{ vars.foo }}` is returned by the extract entry point as `vars.foo`, even though execution and preview rendering treat that occurrence as non-template text. The issue requires the retained preview/extract entry points to share the template behavior vectors, including escape handling; this can cause the editor to mark literal documentation as an active variable and leaves extract behavior inconsistent with rendering.

Make `ExtractVariables` ignore escaped openings using the same escape handling as the renderer, and add a route or engine regression test proving `\${{ vars.foo }}` is not extracted while an unescaped `${{ vars.foo }}` is.

<promise>FAIL</promise>
