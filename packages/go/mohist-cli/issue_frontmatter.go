package mohistcli

import "strings"

// issueFrontmatterKind classifies the leading envelope of an Issue body.
type issueFrontmatterKind int

const (
	// issueFrontmatterNone means the text has no leading "---" delimiter; the
	// full text is the body and no metadata is derived.
	issueFrontmatterNone issueFrontmatterKind = iota
	// issueFrontmatterParsed means a legal closed envelope was stripped.
	issueFrontmatterParsed
	// issueFrontmatterMalformed means an opening delimiter was present but the
	// envelope was unclosed or contained a colon-less metadata line; the full
	// original text is returned as the body.
	issueFrontmatterMalformed
)

// issueFrontmatter is the minimal result of partitioning an Issue body. There
// are no per-field presence booleans: an empty string means "absent", matching
// the legacy NullIfEmpty semantics. recommended_workflow_reason is consumed so
// its block-scalar continuation lines do not leak into later keys, but its
// value is never stored.
type issueFrontmatter struct {
	kind                issueFrontmatterKind
	body                string
	recommendedWorkflow string
	risk                string
}

// partitionIssueFrontmatter classifies text as none/parsed/malformed and, for
// a legal envelope, returns the exact post-envelope body slice. It reads only
// recommended_workflow and risk.
func partitionIssueFrontmatter(text string) issueFrontmatter {
	if text == "" {
		return issueFrontmatter{kind: issueFrontmatterNone}
	}

	lines := strings.Split(text, "\n")
	if !issueFrontmatterIsDelimiter(issueFrontmatterStripBOM(issueFrontmatterStripTrailing(lines[0]))) {
		return issueFrontmatter{kind: issueFrontmatterNone, body: text}
	}

	closing := issueFrontmatterFindClosingDelimiter(lines)
	if closing == -1 {
		return issueFrontmatter{kind: issueFrontmatterMalformed, body: text}
	}

	workflow, risk, malformed := issueFrontmatterParseFields(lines[1:closing])
	if malformed {
		return issueFrontmatter{kind: issueFrontmatterMalformed, body: text}
	}

	return issueFrontmatter{
		kind:                issueFrontmatterParsed,
		body:                issueFrontmatterBodyAfter(text, lines, closing),
		recommendedWorkflow: workflow,
		risk:                risk,
	}
}

func issueFrontmatterFindClosingDelimiter(lines []string) int {
	for i := 1; i < len(lines); i++ {
		if issueFrontmatterIsDelimiter(issueFrontmatterStripTrailing(lines[i])) {
			return i
		}
	}
	return -1
}

// issueFrontmatterParseFields returns the recognized values plus whether the
// metadata block is malformed. Unknown keys, including
// recommended_workflow_reason, are consumed (block scalar included) and
// discarded.
func issueFrontmatterParseFields(lines []string) (string, string, bool) {
	var workflow, risk string
	for i := 0; i < len(lines); i++ {
		raw := issueFrontmatterStripTrailing(lines[i])
		trimmed := strings.TrimSpace(raw)
		if trimmed == "" || strings.HasPrefix(trimmed, "#") {
			continue
		}

		colon := strings.Index(raw, ":")
		if colon < 0 {
			return "", "", true
		}
		key := strings.TrimSpace(raw[:colon])
		if key == "" {
			return "", "", true
		}

		rawValue := strings.TrimSpace(raw[colon+1:])
		var value string
		if rawValue == "|" || rawValue == ">" {
			value = issueFrontmatterReadBlock(lines, &i, rawValue == ">")
		} else {
			value = issueFrontmatterUnquote(rawValue)
		}

		switch key {
		case "recommended_workflow":
			workflow = value
		case "risk":
			risk = value
		}
	}
	return workflow, risk, false
}

// issueFrontmatterReadBlock consumes the indented continuation lines of a
// literal (|) or folded (>) scalar and advances index to the last consumed
// line so the caller resumes at the next unindented key.
func issueFrontmatterReadBlock(lines []string, index *int, folded bool) string {
	var collected []string
	indent := -1
	k := *index + 1
	lastConsumed := *index

	for k < len(lines) {
		line := issueFrontmatterStripTrailing(lines[k])
		if line == "" {
			collected = append(collected, "")
			lastConsumed = k
			k++
			continue
		}
		leading := issueFrontmatterLeadingWhitespace(line)
		if leading == 0 {
			break
		}
		if indent < 0 {
			indent = leading
		}
		collected = append(collected, issueFrontmatterStripIndent(line, indent))
		lastConsumed = k
		k++
	}

	for len(collected) > 0 && collected[len(collected)-1] == "" {
		collected = collected[:len(collected)-1]
	}
	*index = lastConsumed

	if !folded {
		return strings.Join(collected, "\n")
	}
	nonEmpty := collected[:0]
	for _, line := range collected {
		if line != "" {
			nonEmpty = append(nonEmpty, line)
		}
	}
	return strings.Join(nonEmpty, " ")
}

// issueFrontmatterBodyAfter slices text byte-for-byte past the closing
// delimiter line while accounting for LF and CRLF endings.
func issueFrontmatterBodyAfter(text string, lines []string, closing int) string {
	bodyStartLine := closing + 1
	offset := 0
	for i := 0; i < bodyStartLine && i < len(lines); i++ {
		offset += len(lines[i]) + 1
	}
	if offset >= len(text) {
		return ""
	}
	return text[offset:]
}

func issueFrontmatterIsDelimiter(line string) bool { return line == "---" }

func issueFrontmatterStripTrailing(line string) string {
	if strings.HasSuffix(line, "\r") {
		return line[:len(line)-1]
	}
	return line
}

func issueFrontmatterStripBOM(line string) string {
	return strings.TrimPrefix(line, "\uFEFF")
}

func issueFrontmatterLeadingWhitespace(line string) int {
	count := 0
	for _, c := range line {
		if c == ' ' || c == '\t' {
			count++
			continue
		}
		break
	}
	return count
}

func issueFrontmatterStripIndent(line string, count int) string {
	if len(line) <= count {
		return ""
	}
	return line[count:]
}

func issueFrontmatterUnquote(value string) string {
	if len(value) >= 2 {
		first, last := value[0], value[len(value)-1]
		if (first == '"' && last == '"') || (first == '\'' && last == '\'') {
			return value[1 : len(value)-1]
		}
	}
	return value
}
