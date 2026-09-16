package mohistcli

import (
	"context"
	"os"
	"strings"
	"testing"
)

func TestTextCarrierEmptyPathIsReadFailure(t *testing.T) {
	calls := 0
	deps := Dependencies{ReadFile: func(path string) (string, error) {
		calls++
		if path != "" {
			t.Fatalf("path=%q", path)
		}
		return "", os.ErrNotExist
	}}
	value, err := resolveTextInput(deps, command{args: []string{"body-file", ""}}, "body", "body-file")
	if calls != 1 || value != "" || err == nil || !strings.Contains(err.Error(), `could not read --body-file ""`) {
		t.Fatalf("calls=%d value=%q error=%v", calls, value, err)
	}
}

func TestEmptyFilePathCannotBecomeEmptyMutation(t *testing.T) {
	for _, args := range [][]string{
		{"issue", "create", "Title", "--body-file", ""},
		{"issue", "edit", "42", "--body-file", ""},
		{"issue", "edit", "42", "--body-file", "", "--label", "type:bug"},
		{"issue", "comment", "create", "42", "--body-file", ""},
		{"epic", "edit", "1", "--description-file", ""},
		{"agent", "start", "--prompt-file", ""},
		{"session", "followup", "s1", "--text-file", ""},
	} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			f := newCommandFixture(nil)
			code := Run(context.Background(), args, f.deps)
			if code != ExitUsage || !strings.Contains(f.stderr.String(), "could not read --") || f.transport.calls != 0 || len(f.writtenPaths.paths) != 0 {
				t.Fatalf("code=%d stderr=%q HTTP=%d writes=%v", code, f.stderr.String(), f.transport.calls, f.writtenPaths.paths)
			}
			found := false
			for _, path := range f.readPaths.paths {
				if path == "" {
					found = true
				}
			}
			if !found {
				t.Fatal("empty file path was never read")
			}
		})
	}
}
