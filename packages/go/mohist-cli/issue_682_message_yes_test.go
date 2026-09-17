package mohistcli

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"reflect"
	"strings"
	"testing"
)

// TestIssue682MessageAndYesShortFlagsEqualLongFlags proves -m canonicalizes to
// --message and -y to --yes on every leaf that declares the long flag: the
// short and long spellings must produce identical command args.
func TestIssue682MessageAndYesShortFlagsEqualLongFlags(t *testing.T) {
	cases := []struct {
		name  string
		long  []string
		short []string
	}{
		{
			name:  "run request-changes message",
			long:  []string{"run", "request-changes", "--issue", "42", "--message", "msg"},
			short: []string{"run", "request-changes", "--issue", "42", "-m", "msg"},
		},
		{
			name:  "run stop yes",
			long:  []string{"run", "stop", "wr-1", "--yes"},
			short: []string{"run", "stop", "wr-1", "-y"},
		},
		{
			name:  "webhook subscription delete yes",
			long:  []string{"webhook", "subscription", "delete", "sub-1", "--yes"},
			short: []string{"webhook", "subscription", "delete", "sub-1", "-y"},
		},
		{
			name:  "session stop yes",
			long:  []string{"session", "stop", "sess-1", "--idempotency-key", "k", "--yes"},
			short: []string{"session", "stop", "sess-1", "--idempotency-key", "k", "-y"},
		},
		{
			name:  "agent archive yes",
			long:  []string{"agent", "archive", "agent-1", "--yes"},
			short: []string{"agent", "archive", "agent-1", "-y"},
		},
		{
			name:  "agent restore yes",
			long:  []string{"agent", "restore", "agent-1", "--yes"},
			short: []string{"agent", "restore", "agent-1", "-y"},
		},
		{
			name:  "slack permanent-delete yes",
			long:  []string{"slack", "permanent-delete", "sub-1", "--yes"},
			short: []string{"slack", "permanent-delete", "sub-1", "-y"},
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

// TestIssue682ShortMessageReachesRequestChangesBody drives the acceptance
// command through Run and asserts the short flag value lands in the POSTed
// request-changes body.
func TestIssue682ShortMessageReachesRequestChangesBody(t *testing.T) {
	var gotMethod, gotPath string
	var gotBody []byte
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch {
		case r.Method == http.MethodGet && r.URL.Path == "/api/projects/proj/issues/42":
			return response(http.StatusOK, `{"success":true,"data":{"workflowRunId":"wr-1"}}`), nil
		case r.Method == http.MethodPost && r.URL.Path == "/api/workflow-runs/wr-1/request-changes":
			gotMethod, gotPath = r.Method, r.URL.Path
			gotBody, _ = io.ReadAll(r.Body)
			return response(http.StatusOK, `{"success":true,"data":{}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	args := []string{"run", "request-changes", "--project", "proj", "--issue", "42", "-m", "msg"}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if gotMethod != http.MethodPost || gotPath != "/api/workflow-runs/wr-1/request-changes" {
		t.Fatalf("request method=%q path=%q", gotMethod, gotPath)
	}
	var payload map[string]any
	if err := json.Unmarshal(gotBody, &payload); err != nil {
		t.Fatalf("body %q is not JSON: %v", gotBody, err)
	}
	if payload["message"] != "msg" {
		t.Fatalf("message=%v body=%s", payload["message"], gotBody)
	}
}

// TestIssue682ShortYesConfirmsRunStop proves -y satisfies the irreversible
// confirmation gate: it reaches the stop request, while the unflagged form
// stays local with no request.
func TestIssue682ShortYesConfirmsRunStop(t *testing.T) {
	var posts int
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch {
		case r.Method == http.MethodGet && r.URL.Path == "/api/projects/proj/issues/42":
			return response(http.StatusOK, `{"success":true,"data":{"workflowRunId":"wr-1"}}`), nil
		case r.Method == http.MethodPost && r.URL.Path == "/api/workflow-runs/wr-1/stop":
			posts++
			return response(http.StatusOK, `{"success":true,"data":{}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "stop", "--project", "proj", "--issue", "42", "-y"}, deps); code != ExitOK {
		t.Fatalf("short -y code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if posts != 1 {
		t.Fatalf("stop requests=%d", posts)
	}

	calls := 0
	blockedDeps, _, blockedErr := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"run", "stop", "--project", "proj", "--issue", "42"}, blockedDeps); code != ExitOperation {
		t.Fatalf("unconfirmed code=%d stderr=%q", code, blockedErr.String())
	}
	if calls != 0 || !strings.Contains(blockedErr.String(), "--yes") {
		t.Fatalf("unconfirmed calls=%d stderr=%q", calls, blockedErr.String())
	}
}

// TestIssue682WrongLeafAndUnknownShortFlagsFailLocally proves the new short
// spellings stay scoped to the leaves that declare their long flag and that an
// unknown short flag exits 2 before any request.
func TestIssue682WrongLeafAndUnknownShortFlagsFailLocally(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{name: "message on issue create", args: []string{"issue", "create", "Title", "--body", "b", "-m", "x", "--project", "proj"}},
		{name: "yes on runner list", args: []string{"runner", "list", "-y", "--project", "proj"}},
		{name: "label on run stop", args: []string{"run", "stop", "wr-1", "-l", "x"}},
		{name: "unknown short flag on run stop", args: []string{"run", "stop", "wr-1", "-x"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			calls := 0
			deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return response(http.StatusInternalServerError, `{}`), nil
			}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
			if code := Run(context.Background(), tc.args, deps); code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if calls != 0 {
				t.Fatalf("HTTP requests issued=%d", calls)
			}
			if !strings.Contains(errOut.String(), "unknown option") {
				t.Fatalf("stderr=%q", errOut.String())
			}
		})
	}
}

// TestIssue682MessageShortFlagBeforeDiscoveryKeepsValue mirrors the T-001
// discovery guard for -m: the token after the short value flag is a value, not
// a discovery request.
func TestIssue682MessageShortFlagBeforeDiscoveryKeepsValue(t *testing.T) {
	for _, token := range []string{"--help", "--json"} {
		t.Run(token, func(t *testing.T) {
			args := []string{"run", "request-changes", "--issue", "42", "-m", token}
			cmd, err := parse(args)
			if err != nil || cmd.help || cmd.fieldsOnly || argValue(cmd.args, "message", "") != token {
				t.Fatalf("command=%+v error=%v", cmd, err)
			}
		})
	}
}
