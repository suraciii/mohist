package mohistslack

import (
	"errors"
	"strings"
	"testing"
)

func TestSafeErrorMessageRedactsTokenShapes(t *testing.T) {
	err := errors.New("post failed with xoxb-1234-abcdSECRET and xapp.1-aB_c and xoxp:x and xoxe~z")
	redacted := SafeErrorMessage(err)
	for _, shape := range []string{"xoxb", "xapp", "xoxp", "xoxe"} {
		if strings.Contains(redacted, shape+"-") || strings.Contains(redacted, shape+".") ||
			strings.Contains(redacted, shape+":") || strings.Contains(redacted, shape+"~") {
			t.Fatalf("redaction leaked token shape %q in %q", shape, redacted)
		}
	}
	if !strings.Contains(redacted, "<redacted>") {
		t.Fatalf("redacted message lost placeholder: %q", redacted)
	}
}

func TestLoggerSelectsTextAndJSONHandlers(t *testing.T) {
	for _, testCase := range []struct {
		name   string
		format string
		prefix string
	}{
		{name: "text", format: "text", prefix: "time="},
		{name: "json", format: "json", prefix: "{"},
	} {
		t.Run(testCase.name, func(t *testing.T) {
			var buffer strings.Builder
			logger := NewLogger(&buffer, testCase.format)
			logger.Error("delivery failed", "reason", SafeErrorMessage(errors.New("boom xoxb-secret-value")))
			line := buffer.String()
			if !strings.HasPrefix(line, testCase.prefix) {
				t.Fatalf("log line = %q", line)
			}
			if strings.Contains(line, "xoxb-secret-value") || !strings.Contains(line, "<redacted>") {
				t.Fatalf("log redaction failed: %s", line)
			}
		})
	}
}
