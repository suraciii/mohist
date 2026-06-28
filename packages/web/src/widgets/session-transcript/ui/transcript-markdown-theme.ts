/**
 * Lightweight syntax-highlight theme for `.transcript-md`.
 *
 * Source: `highlight.js/styles/github.css` (highlight.js v11.11+, "GitHub" light theme).
 * The CSS is embedded verbatim here because Vitest's Vite transform pipeline
 * does not handle the `?raw`/`?inline` suffix for CSS files in node_modules
 * (it returns an empty string), and we need this module to evaluate identically
 * in production Vite and in jsdom-driven Vitest runs.
 *
 * Every top-level selector below is prefixed with `.transcript-md` so the
 * theme does not leak into other markdown surfaces (e.g. the shared
 * `MarkdownReader`). See `scopeFlattenCss` for the selector-prefixing logic.
 */
const HIGHLIGHT_JS_GITHUB_THEME_CSS = `pre code.hljs {
  display: block;
  overflow-x: auto;
  padding: 1em
}
code.hljs {
  padding: 3px 5px
}
/*!
  Theme: GitHub
  Description: Light theme as seen on github.com
  Author: github.com
  Maintainer: @Hirse
  Updated: 2021-05-15

  Outdated base version: https://github.com/primer/github-syntax-light
  Current colors taken from GitHub's CSS
*/
.hljs {
  color: #24292e;
  background: #ffffff
}
.hljs-doctag,
.hljs-keyword,
.hljs-meta .hljs-keyword,
.hljs-template-tag,
.hljs-template-variable,
.hljs-type,
.hljs-variable.language_ {
  /* prettylights-syntax-keyword */
  color: #d73a49
}
.hljs-title,
.hljs-title.class_,
.hljs-title.class_.inherited__,
.hljs-title.function_ {
  /* prettylights-syntax-entity */
  color: #6f42c1
}
.hljs-attr,
.hljs-attribute,
.hljs-literal,
.hljs-meta,
.hljs-number,
.hljs-operator,
.hljs-variable,
.hljs-selector-attr,
.hljs-selector-class,
.hljs-selector-id {
  /* prettylights-syntax-constant */
  color: #005cc5
}
.hljs-regexp,
.hljs-string,
.hljs-meta .hljs-string {
  /* prettylights-syntax-string */
  color: #032f62
}
.hljs-built_in,
.hljs-symbol {
  /* prettylights-syntax-variable */
  color: #e36209
}
.hljs-comment,
.hljs-code,
.hljs-formula {
  /* prettylights-syntax-comment */
  color: #6a737d
}
.hljs-name,
.hljs-quote,
.hljs-selector-tag,
.hljs-selector-pseudo {
  /* prettylights-syntax-entity-tag */
  color: #22863a
}
.hljs-subst {
  /* prettylights-syntax-storage-modifier-import */
  color: #24292e
}
.hljs-section {
  /* prettylights-syntax-markup-heading */
  color: #005cc5;
  font-weight: bold
}
.hljs-bullet {
  /* prettylights-syntax-markup-list */
  color: #735c0f
}
.hljs-emphasis {
  /* prettylights-syntax-markup-italic */
  color: #24292e;
  font-style: italic
}
.hljs-strong {
  /* prettylights-syntax-markup-bold */
  color: #24292e;
  font-weight: bold
}
.hljs-addition {
  /* prettylights-syntax-markup-inserted */
  color: #22863a;
  background-color: #f0fff4
}
.hljs-deletion {
  /* prettylights-syntax-markup-deleted */
  color: #b31d28;
  background-color: #ffeef0
}
.hljs-char.escape_,
.hljs-link,
.hljs-params,
.hljs-property,
.hljs-punctuation,
.hljs-tag {
  color: inherit
}`

const TRANSCRIPT_MD_SCOPE = '.transcript-md'

function stripCssComments(css: string): string {
  let out = ''
  let i = 0
  while (i < css.length) {
    if (css[i] === '/' && css[i + 1] === '*') {
      const end = css.indexOf('*/', i + 2)
      if (end < 0) break
      i = end + 2
      continue
    }
    out += css[i]
    i += 1
  }
  return out
}

function prefixSelector(selector: string, scope: string): string {
  const trimmed = selector.trim()
  if (!trimmed) return ''
  if (trimmed.startsWith(scope)) return trimmed
  return `${scope} ${trimmed}`
}

/**
 * Flatten a flat CSS stylesheet by prefixing every top-level selector
 * (in comma-separated selector lists) with `scope`. Nested rules,
 * media queries, and other at-rules are NOT supported — the github.css
 * theme is a flat selector+block stylesheet, which is the only shape
 * we need to handle.
 */
function scopeFlattenCss(css: string, scope: string): string {
  const cleaned = stripCssComments(css)
  const out: string[] = []
  let depth = 0
  let buffer = ''

  const flush = () => {
    const open = buffer.indexOf('{')
    if (open < 0) return
    const selectorList = buffer.slice(0, open)
    const close = buffer.lastIndexOf('}')
    const decls = buffer.slice(open + 1, close < 0 ? undefined : close)
    const selectors = selectorList
      .split(',')
      .map((part) => prefixSelector(part, scope))
      .filter(Boolean)
    if (selectors.length > 0 && decls.trim()) {
      out.push(`${selectors.join(',\n')} {${decls}}`)
    }
    buffer = ''
  }

  for (let i = 0; i < cleaned.length; i += 1) {
    const ch = cleaned[i]
    if (ch === '{') {
      depth += 1
      buffer += ch
    } else if (ch === '}') {
      depth -= 1
      buffer += ch
      if (depth === 0) {
        flush()
      }
    } else {
      buffer += ch
    }
  }

  return out.join('\n')
}

export const TRANSCRIPT_MD_HIGHLIGHT_CSS = scopeFlattenCss(HIGHLIGHT_JS_GITHUB_THEME_CSS, TRANSCRIPT_MD_SCOPE)
