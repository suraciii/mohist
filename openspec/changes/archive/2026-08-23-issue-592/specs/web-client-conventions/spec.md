### Requirement: Sonner is web's only toast system

Web SHALL route every user-facing toast notification through sonner. The self-built toast system (`RuntimeToastHost`, its context, and the `shared/ui/toast` module) MUST NOT exist, and the application root MUST NOT mount a toast host other than sonner's `Toaster`. No production module SHALL push toasts through a custom context.

#### Scenario: Showing a runtime notification

- **WHEN** production code shows a success, error, or informational toast
- **THEN** it SHALL call sonner's `toast` API, rendered by the `Toaster` mounted in the app content
- **AND** no parallel toast host component SHALL be mounted in the application tree

#### Scenario: The self-built toast host is gone

- **WHEN** the web source tree is searched for `RuntimeToastHost`, `useRuntimeToast`, or the `shared/ui/toast` module
- **THEN** no production module or test SHALL reference them

### Requirement: MarkdownReader is web's only markdown renderer

Web SHALL render production markdown through `MarkdownReader` in `shared/ui/markdown-reader`. The retired `markdown-content.tsx` component MUST NOT exist. `MarkdownReader` SHALL NOT carry presentation toggles that no production call site enables: the former `showToc`, `showHeadingAnchors`, and `showCopyCode` props, the table-of-contents rendering, heading-anchor rendering, and the copy-code affordance (with its `copy-code-button` module) MUST NOT exist. Base heading-level remapping, attachment resolution, table containment, and collapsible mode SHALL keep working.

#### Scenario: Rendering markdown in production surfaces

- **WHEN** a page or widget renders issue descriptions, comments, epic descriptions, or artifact text
- **THEN** it SHALL render through `MarkdownReader` with its production props (`content`, `baseHeadingLevel`, `mode`, `collapsedHeight`, `resolveAttachment`)

#### Scenario: The retired renderer and dead affordances are gone

- **WHEN** the web source tree is searched for `markdown-content` / `MarkdownContent`, `showToc`, `showHeadingAnchors`, `showCopyCode`, or `CopyCodeButton`
- **THEN** no production module or test SHALL reference them

### Requirement: useMediaQuery is web's only viewport-detection hook

Web SHALL detect viewport breakpoints through exactly one hook: `useMediaQuery` in `shared/lib/use-media-query`. The former `useNarrowViewport` and `useIsMobile` hooks MUST NOT exist, and their former usage points SHALL call `useMediaQuery` with the equivalent media query (or, where the consuming branch itself was deleted, use no viewport hook at all).

#### Scenario: Detecting a viewport breakpoint

- **WHEN** a component needs to know whether the viewport is narrow or mobile
- **THEN** it SHALL call `useMediaQuery` with an explicit media query
- **AND** no parallel viewport-detection hook SHALL exist in `shared`

### Requirement: The query dual-write convention is documented

`packages/web/AGENTS.md` SHALL document the dual-write convention for entity query clients: every server read is written twice — an exported query-options factory that owns the query key and fetch behavior, plus a thin hook wrapping `useQuery` over that factory — with invalidation and prefetch reusing the same factory so query keys stay single-sourced, and the same pairing (client function + mutation-options factory + thin hook) for mutations. The unified session clients in `entities/coder-session/api/client.ts` SHALL be cited as the reference implementation, and those clients SHALL keep exposing the factory + thin-hook pairing.

#### Scenario: The convention is documented

- **WHEN** `packages/web/AGENTS.md` is read after this change
- **THEN** it SHALL contain the dual-write convention (options factory + thin hook, single-sourced query keys, invalidation/prefetch through the factory, same pairing for mutations)
- **AND** it SHALL name the unified session clients as the reference implementation

#### Scenario: The reference implementation keeps the pairing

- **WHEN** the unified session clients in `entities/coder-session/api/client.ts` are inspected after this change
- **THEN** `unifiedSessionSummaryQueryOptions` / `unifiedSessionTranscriptQueryOptions` and their `useUnifiedSessionSummary` / `useUnifiedSessionTranscript` hooks SHALL both be exported
