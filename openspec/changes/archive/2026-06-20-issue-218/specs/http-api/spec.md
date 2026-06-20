## ADDED Requirements

### Requirement: API responses preserve non-ASCII characters verbatim

Outbound HTTP API responses SHALL preserve non-ASCII characters (e.g. Chinese) in their original form across every response path — including success responses via `Results.Ok` / `Results.Json`, shared `ApiResults.*` helpers, and unhandled-exception middleware responses. Responses SHALL NOT encode non-ASCII characters as `\uXXXX` escape sequences.

#### Scenario: Success response preserves non-ASCII

- **WHEN** a client requests an endpoint whose response payload contains non-ASCII text (e.g. an issue title `修复中文乱码`)
- **THEN** the response body SHALL contain the original characters verbatim
- **AND** the response body SHALL NOT contain `\uXXXX` escape sequences for those characters

#### Scenario: Error response preserves non-ASCII

- **WHEN** an API error response message contains non-ASCII text
- **THEN** the response body SHALL contain the original characters verbatim
- **AND** the response body SHALL NOT contain `\uXXXX` escape sequences for those characters
