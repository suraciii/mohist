# Review Findings

## Findings

### 1. [blocker] Default OpenSpec tasks now fail when `prompts.build` is absent

`packages/runner/src/actions/openspec.ts:324-338` unconditionally adds `base: "${{ prompts.build }}"` to every generated default prompt-loader spec. Before this change, the loader only received a `base` field when `prompts.build` existed in the dispatched variables; otherwise the field was omitted and the generated task still ran. The new child task dispatch treats the prompt object as an immediate input, so a missing `prompts.build` is reported as an unresolved template and the default `mohist/opencode` task fails input validation. Preserve the old conditional behavior while still carrying the deferred task file/items/task selector through the result effect.

### 2. [major] Manifest-specific host typing is defined but not used by Action execution

`packages/runner/src/actions/host.ts:59-67` defines `ActionHostFor<M>` to make capability members depend on the selected manifest, but `packages/runner/src/actions/manifest.ts:56-59` types every `ActionDefinition.run` as receiving the unrestricted `ActionHost`, whose `agent`, `issue`, and `checkpoint` members are all visible to every Action. Consequently an Action with no declared capabilities can still compile against those capability APIs, and a declared Action does not get a manifest-derived execution type. This leaves the capability boundary runtime-only and contradicts the design requirement that the host be type-checkable and that available capabilities equal the manifest declaration. Make the Action definition/run and registration path use the manifest-specific host type, with declared capabilities required and undeclared capability members absent.

<promise>FAIL</promise>
