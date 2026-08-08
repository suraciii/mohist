# Agents — Writing Product Docs

`docs/` is the product spec layer: what the product must satisfy. Written for users and agents; readers do not read source.

## Rules

- Write the spec before implementing.
- Use product + domain language only. No technical language (API, fields, components, source paths).
- One section, one purpose. Lists over paragraphs, tables over lists.
- Define a rule once; other docs link it, never copy it.
- Commands and examples must be runnable as written.
- Body is the spec; the gap is the footnote — divergence is a gap section, never a status list.
- Keep terms consistent with [`CONTEXT.md`](../CONTEXT.md).
- Use English, ASD-STE100 style: short sentences, active voice, `must` / `may`.
- WIP product ideas stay in a WIP section with `status: wip-not-implemented`.

Full conventions: [`docs/README.md`](README.md).
