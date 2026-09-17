package mohistcli

import (
	"context"
	"reflect"
	"strings"
	"testing"
)

// TestIssue682ServiceAndInfoShortFlagsEqualLongFlags proves -n canonicalizes
// to --lines, -f to --follow, and -v to --verbose on the leaves that declare
// the long spelling: short and long forms must produce identical command args.
func TestIssue682ServiceAndInfoShortFlagsEqualLongFlags(t *testing.T) {
	cases := []struct {
		name  string
		long  []string
		short []string
	}{
		{
			name:  "service logs lines and follow",
			long:  []string{"service", "logs", "server", "--lines", "25", "--follow"},
			short: []string{"service", "logs", "server", "-n", "25", "-f"},
		},
		{
			name:  "info verbose",
			long:  []string{"info", "--verbose"},
			short: []string{"info", "-v"},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			long, err := parse(tc.long)
			if err != nil {
				t.Fatalf("parse(long %v): %v", tc.long, err)
			}
			short, err := parse(tc.short)
			if err != nil {
				t.Fatalf("parse(short %v): %v", tc.short, err)
			}
			if long.kind != short.kind {
				t.Fatalf("kind long=%q short=%q", long.kind, short.kind)
			}
			if !reflect.DeepEqual(long.args, short.args) {
				t.Fatalf("args differ: long=%v short=%v", long.args, short.args)
			}
		})
	}
}

// TestIssue682ServiceLogsShortFlagsReachJournalctl drives the acceptance
// command end to end and asserts the short flags land in the journalctl
// argument list exactly like their long spellings.
func TestIssue682ServiceLogsShortFlagsReachJournalctl(t *testing.T) {
	deps, out, errOut := testDeps(nil, map[string]string{})
	var gotName string
	var gotArgs []string
	deps.ExecuteOutput = func(_ context.Context, name string, args []string) (string, error) {
		gotName, gotArgs = name, args
		return "server log\n", nil
	}
	if code := Run(context.Background(), []string{"service", "logs", "server", "-n", "25", "-f"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	want := []string{"--user", "-u", "mohist.service", "--no-pager", "-n", "25", "-f"}
	if gotName != "journalctl" || !reflect.DeepEqual(gotArgs, want) {
		t.Fatalf("command=%s %#v, want journalctl %#v", gotName, gotArgs, want)
	}
	if out.String() != "server log\n" {
		t.Fatalf("stdout=%q", out.String())
	}
}

// TestIssue682InfoVerboseOutputUnchanged proves the -v spelling routes to the
// existing verbose output path and produces the same text as --verbose.
func TestIssue682InfoVerboseOutputUnchanged(t *testing.T) {
	run := func(args []string) string {
		deps, out, errOut := testDeps(nil, map[string]string{})
		if code := Run(context.Background(), args, deps); code != ExitOK {
			t.Fatalf("%v code=%d stdout=%q stderr=%q", args, code, out.String(), errOut.String())
		}
		return out.String()
	}
	short := run([]string{"info", "-v"})
	long := run([]string{"info", "--verbose"})
	if short != long {
		t.Fatalf("-v output=%q --verbose output=%q", short, long)
	}
	if !strings.Contains(short, "Data directory: unknown") {
		t.Fatalf("-v did not take the verbose path: %q", short)
	}
}

// TestIssue682InfoAndServiceLogsHelpRenderBothSpellings pins the leaf help so
// both spellings stay visible, matching the reference allowlist.
func TestIssue682InfoAndServiceLogsHelpRenderBothSpellings(t *testing.T) {
	cases := []struct {
		name       string
		args       []string
		wantStdout []string
	}{
		{
			name:       "info help",
			args:       []string{"info", "--help"},
			wantStdout: []string{"[-v, --verbose]"},
		},
		{
			name:       "service logs help",
			args:       []string{"service", "logs", "server", "--help"},
			wantStdout: []string{"[-n, --lines N]", "[-f, --follow]"},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			deps, out, errOut := testDeps(nil, map[string]string{})
			if code := Run(context.Background(), tc.args, deps); code != ExitOK {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			for _, want := range tc.wantStdout {
				if !strings.Contains(out.String(), want) {
					t.Fatalf("stdout=%q, want %q", out.String(), want)
				}
			}
		})
	}
}

// TestIssue682InfoAndServiceLogsUnknownOptionUsageRenderBothSpellings proves
// the usage block echoed on an unknown option matches the help spelling.
func TestIssue682InfoAndServiceLogsUnknownOptionUsageRenderBothSpellings(t *testing.T) {
	cases := []struct {
		name string
		args []string
		want []string
	}{
		{
			name: "info",
			args: []string{"info", "--bogus"},
			want: []string{"usage: mo info [-v, --verbose] [--json [fields]]"},
		},
		{
			name: "service logs",
			args: []string{"service", "logs", "server", "--bogus"},
			want: []string{"mo service logs server [--dry-run] [-n, --lines N] [-f, --follow]"},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			deps, out, errOut := testDeps(nil, map[string]string{})
			if code := Run(context.Background(), tc.args, deps); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if out.Len() != 0 || !strings.Contains(errOut.String(), "unknown option") {
				t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
			}
			for _, want := range tc.want {
				if !strings.Contains(errOut.String(), want) {
					t.Fatalf("stderr=%q, want %q", errOut.String(), want)
				}
			}
		})
	}
}

// TestIssue682ServiceWrongLeafFlagsStayLocal proves -f/-n do not widen to
// actions that never declared --follow/--lines: both spellings exit 2 before
// any service command runs.
func TestIssue682ServiceWrongLeafFlagsStayLocal(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{name: "start follow long", args: []string{"service", "start", "server", "--follow"}},
		{name: "start follow short", args: []string{"service", "start", "server", "-f"}},
		{name: "status lines long", args: []string{"service", "status", "server", "--lines", "5"}},
		{name: "status lines short", args: []string{"service", "status", "server", "-n", "5"}},
		{name: "logs unit-dir", args: []string{"service", "logs", "server", "--unit-dir", "/units"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			deps, _, errOut := testDeps(nil, map[string]string{})
			executed := 0
			deps.Execute = func(context.Context, string, []string) error {
				executed++
				return nil
			}
			deps.ExecuteOutput = func(context.Context, string, []string) (string, error) {
				executed++
				return "", nil
			}
			if code := Run(context.Background(), tc.args, deps); code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if executed != 0 {
				t.Fatalf("service command executed %d times", executed)
			}
			if !strings.Contains(errOut.String(), "unknown option") {
				t.Fatalf("stderr=%q", errOut.String())
			}
		})
	}
}
