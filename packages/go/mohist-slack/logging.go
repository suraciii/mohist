package mohistslack

import "regexp"

// slackTokenPattern matches Slack token shapes in free-form error text.
var slackTokenPattern = regexp.MustCompile(`(?i)(?:xapp|xoxb|xoxp|xoxe)[.A-Za-z0-9_-]*`)

// RedactTokens masks Slack token shapes before a message reaches logs.
func RedactTokens(message string) string {
	return slackTokenPattern.ReplaceAllString(message, "<redacted>")
}
