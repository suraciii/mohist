package mohistcli

import (
	"strings"
	"testing"
)

// issueFrontmatterResult is a small test helper that keeps the assertions
// readable without exposing presence booleans on the production result.
func assertParsedFrontmatter(t *testing.T, text string) issueFrontmatter {
	t.Helper()
	fm := partitionIssueFrontmatter(text)
	if fm.kind != issueFrontmatterParsed {
		t.Fatalf("kind=%d want parsed for %q", fm.kind, text)
	}
	return fm
}

func TestIssueFrontmatterCompleteEnvelopeExtractsFieldsAndStripsBody(t *testing.T) {
	text := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"recommended_workflow_reason: Matches UI and feature scope\n" +
		"risk: high\n" +
		"---\n" +
		"## Background\n" +
		"Real body content.\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.recommendedWorkflow != "feature-flow" {
		t.Fatalf("workflow=%q", fm.recommendedWorkflow)
	}
	if fm.risk != "high" {
		t.Fatalf("risk=%q", fm.risk)
	}
	if fm.body != "## Background\nReal body content.\n" {
		t.Fatalf("body=%q", fm.body)
	}
}

func TestIssueFrontmatterLiteralBlockScalarReasonThenRisk(t *testing.T) {
	text := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"recommended_workflow_reason: |\n" +
		"  Line one of the reason.\n" +
		"  Line two of the reason.\n" +
		"risk: medium\n" +
		"---\n" +
		"Body only.\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.recommendedWorkflow != "feature-flow" {
		t.Fatalf("workflow=%q", fm.recommendedWorkflow)
	}
	// The reason value is consumed but never stored; risk after the block
	// proves the continuation lines did not swallow later keys.
	if fm.risk != "medium" {
		t.Fatalf("risk=%q", fm.risk)
	}
	if fm.body != "Body only.\n" {
		t.Fatalf("body=%q", fm.body)
	}
}

func TestIssueFrontmatterFoldedBlockScalarReasonThenRisk(t *testing.T) {
	text := "---\n" +
		"recommended_workflow_reason: >\n" +
		"  Folded line one.\n" +
		"  Folded line two.\n" +
		"risk: high\n" +
		"---\n" +
		"Body.\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.risk != "high" {
		t.Fatalf("risk=%q", fm.risk)
	}
	if fm.body != "Body.\n" {
		t.Fatalf("body=%q", fm.body)
	}
}

func TestIssueFrontmatterBlockScalarOnStoredFields(t *testing.T) {
	literal := "---\n" +
		"risk: |\n" +
		"  line one\n" +
		"  line two\n" +
		"---\n" +
		"Body.\n"
	fm := assertParsedFrontmatter(t, literal)
	if fm.risk != "line one\nline two" {
		t.Fatalf("literal risk=%q", fm.risk)
	}

	folded := "---\n" +
		"risk: >\n" +
		"  line one\n" +
		"  line two\n" +
		"---\n" +
		"Body.\n"
	fm = assertParsedFrontmatter(t, folded)
	if fm.risk != "line one line two" {
		t.Fatalf("folded risk=%q", fm.risk)
	}
}

func TestIssueFrontmatterPartialEnvelopeLeavesAbsentFieldsEmpty(t *testing.T) {
	text := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"---\n" +
		"Body.\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.recommendedWorkflow != "feature-flow" {
		t.Fatalf("workflow=%q", fm.recommendedWorkflow)
	}
	if fm.risk != "" {
		t.Fatalf("risk=%q want empty", fm.risk)
	}
	if fm.body != "Body.\n" {
		t.Fatalf("body=%q", fm.body)
	}
}

func TestIssueFrontmatterEmptyScalarMeansAbsent(t *testing.T) {
	text := "---\n" +
		"recommended_workflow:\n" +
		"risk: high\n" +
		"---\n" +
		"Body.\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.recommendedWorkflow != "" {
		t.Fatalf("workflow=%q want empty", fm.recommendedWorkflow)
	}
	if fm.risk != "high" {
		t.Fatalf("risk=%q", fm.risk)
	}
}

func TestIssueFrontmatterUnrecognizedFieldsAreIgnored(t *testing.T) {
	text := "---\n" +
		"title: ignored\n" +
		"recommended_workflow: feature-flow\n" +
		"custom_field: whatever\n" +
		"risk: low\n" +
		"---\n" +
		"Body.\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.recommendedWorkflow != "feature-flow" {
		t.Fatalf("workflow=%q", fm.recommendedWorkflow)
	}
	if fm.risk != "low" {
		t.Fatalf("risk=%q", fm.risk)
	}
	if fm.body != "Body.\n" {
		t.Fatalf("body=%q", fm.body)
	}
}

func TestIssueFrontmatterQuotedValuesStripSurroundingQuotes(t *testing.T) {
	text := "---\n" +
		"recommended_workflow: \"feature-flow\"\n" +
		"risk: 'high'\n" +
		"---\n" +
		"Body.\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.recommendedWorkflow != "feature-flow" {
		t.Fatalf("workflow=%q", fm.recommendedWorkflow)
	}
	if fm.risk != "high" {
		t.Fatalf("risk=%q", fm.risk)
	}
}

func TestIssueFrontmatterNoLeadingDelimiterReturnsNoneWithFullText(t *testing.T) {
	text := "## Just a body\nno frontmatter at all\n"
	fm := partitionIssueFrontmatter(text)
	if fm.kind != issueFrontmatterNone {
		t.Fatalf("kind=%d want none", fm.kind)
	}
	if fm.body != text {
		t.Fatalf("body=%q want full text", fm.body)
	}
}

func TestIssueFrontmatterEmptyInputReturnsNone(t *testing.T) {
	fm := partitionIssueFrontmatter("")
	if fm.kind != issueFrontmatterNone {
		t.Fatalf("kind=%d want none", fm.kind)
	}
	if fm.body != "" {
		t.Fatalf("body=%q want empty", fm.body)
	}
}

func TestIssueFrontmatterHorizontalRuleInBodyIsNotFrontmatter(t *testing.T) {
	text := "# Title\n\n---\n\na horizontal rule, not frontmatter\n"
	fm := partitionIssueFrontmatter(text)
	if fm.kind != issueFrontmatterNone {
		t.Fatalf("kind=%d want none", fm.kind)
	}
	if fm.body != text {
		t.Fatalf("body=%q", fm.body)
	}
}

func TestIssueFrontmatterMissingClosingDelimiterIsMalformed(t *testing.T) {
	text := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"no closing delimiter here\n"

	fm := partitionIssueFrontmatter(text)
	if fm.kind != issueFrontmatterMalformed {
		t.Fatalf("kind=%d want malformed", fm.kind)
	}
	if fm.body != text {
		t.Fatalf("body=%q want full text", fm.body)
	}
	if fm.recommendedWorkflow != "" || fm.risk != "" {
		t.Fatalf("partial metadata leaked: workflow=%q risk=%q", fm.recommendedWorkflow, fm.risk)
	}
}

func TestIssueFrontmatterLineWithoutColonIsMalformed(t *testing.T) {
	text := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"this line has no colon\n" +
		"---\n" +
		"Body.\n"

	fm := partitionIssueFrontmatter(text)
	if fm.kind != issueFrontmatterMalformed {
		t.Fatalf("kind=%d want malformed", fm.kind)
	}
	if fm.body != text {
		t.Fatalf("body=%q want full text", fm.body)
	}
	if fm.recommendedWorkflow != "" || fm.risk != "" {
		t.Fatalf("partial metadata leaked: workflow=%q risk=%q", fm.recommendedWorkflow, fm.risk)
	}
}

func TestIssueFrontmatterBodyOnlyClosedEnvelopeIsEmpty(t *testing.T) {
	text := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"risk: high\n" +
		"---\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.body != "" {
		t.Fatalf("body=%q want empty", fm.body)
	}
	if fm.recommendedWorkflow != "feature-flow" || fm.risk != "high" {
		t.Fatalf("workflow=%q risk=%q", fm.recommendedWorkflow, fm.risk)
	}
}

func TestIssueFrontmatterBOMOpeningDelimiterParsesLikePlain(t *testing.T) {
	plain := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"risk: high\n" +
		"---\n" +
		"Body.\n"
	withBOM := "\uFEFF" + plain

	plainFM := assertParsedFrontmatter(t, plain)
	bomFM := assertParsedFrontmatter(t, withBOM)

	if bomFM.recommendedWorkflow != plainFM.recommendedWorkflow || bomFM.risk != plainFM.risk {
		t.Fatalf("BOM metadata mismatch: %+v vs %+v", bomFM, plainFM)
	}
	if bomFM.body != "Body.\n" {
		t.Fatalf("BOM body=%q want %q", bomFM.body, "Body.\n")
	}
	if strings.HasPrefix(bomFM.body, "\uFEFF") {
		t.Fatalf("BOM leaked into body: %q", bomFM.body)
	}
}

func TestIssueFrontmatterCRLFPreservesBodyBytes(t *testing.T) {
	text := "---\r\n" +
		"recommended_workflow: feature-flow\r\n" +
		"risk: high\r\n" +
		"---\r\n" +
		"Body.\r\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.recommendedWorkflow != "feature-flow" || fm.risk != "high" {
		t.Fatalf("workflow=%q risk=%q", fm.recommendedWorkflow, fm.risk)
	}
	if fm.body != "Body.\r\n" {
		t.Fatalf("body=%q want %q", fm.body, "Body.\r\n")
	}
}

func TestIssueFrontmatterCRLFBOMEnvelopeBodyStartsAfterDelimiter(t *testing.T) {
	text := "\uFEFF---\r\n" +
		"recommended_workflow: feature-flow\r\n" +
		"---\r\n" +
		"Line one\r\n" +
		"Line two\r\n"

	fm := assertParsedFrontmatter(t, text)
	if fm.body != "Line one\r\nLine two\r\n" {
		t.Fatalf("body=%q", fm.body)
	}
}
