package mohistcli

import (
	"context"
	"encoding/json"
	"net/http"
	"strings"
	"testing"
)

const issue1054ThreadViewJSON = `{"success":true,"data":{"thread":{"workspaceTeamId":"T1","connectionId":"K1","channelId":"C1","rootMessageId":"1710.000100"},"messages":[{"ts":"1710.000100","authorUserId":"U1","text":"ship it","threadRoot":true,"edited":false,"deleted":false,"unavailableContent":[]}],"continuation":null},"error":null,"code":null,"details":null}`

func TestIssue1054SlackThreadViewReadsTheBoundThreadPage(t *testing.T) {
	var request *http.Request
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, issue1054ThreadViewJSON), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "operator-token"})

	code := Run(context.Background(), []string{
		"slack", "thread", "view", "--project", "proj", "--session", "session-1", "--limit", "20",
		"--json", "thread,messages,continuation",
	}, deps)

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.Method != http.MethodGet {
		t.Fatalf("request=%v", request)
	}
	if request.URL.Path != "/api/projects/proj/slack-connections/thread" {
		t.Fatalf("path=%q", request.URL.Path)
	}
	query := request.URL.Query()
	if query.Get("sessionId") != "session-1" || query.Get("limit") != "20" {
		t.Fatalf("query=%v", query)
	}
	if query.Has("continuation") {
		t.Fatalf("query=%v, want no continuation", query)
	}
	var selected map[string]json.RawMessage
	if err := json.Unmarshal([]byte(strings.TrimSpace(out.String())), &selected); err != nil {
		t.Fatalf("stdout=%q err=%v", out.String(), err)
	}
	if len(selected) != 3 || selected["thread"] == nil || selected["messages"] == nil {
		t.Fatalf("stdout=%q, want exactly thread, messages, continuation", out.String())
	}
	if string(selected["continuation"]) != "null" {
		t.Fatalf("continuation=%s", selected["continuation"])
	}
}

func TestIssue1054SlackThreadViewSendsTheContinuation(t *testing.T) {
	var request *http.Request
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		request = r
		return response(http.StatusOK, issue1054ThreadViewJSON), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "operator-token"})

	code := Run(context.Background(), []string{
		"slack", "thread", "view", "--project", "proj", "--session", "session-1",
		"--continuation", "opaque-continuation",
	}, deps)

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if request == nil || request.URL.Query().Get("continuation") != "opaque-continuation" {
		t.Fatalf("request=%v", request)
	}
}

func TestIssue1054SlackThreadViewValidatesSessionAndLimitLocally(t *testing.T) {
	calls := 0
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "operator-token"})

	cases := []struct {
		name string
		args []string
		want string
	}{
		{name: "missing-session", args: []string{"slack", "thread", "view", "--project", "proj"}, want: "--session"},
		{name: "zero-limit", args: []string{"slack", "thread", "view", "--project", "proj", "--session", "s1", "--limit", "0"}, want: "between 1 and 100"},
		{name: "over-limit", args: []string{"slack", "thread", "view", "--project", "proj", "--session", "s1", "--limit", "101"}, want: "between 1 and 100"},
		{name: "non-numeric-limit", args: []string{"slack", "thread", "view", "--project", "proj", "--session", "s1", "--limit", "many"}, want: "between 1 and 100"},
		{name: "unknown-json-field", args: []string{"slack", "thread", "view", "--project", "proj", "--session", "s1", "--json", "thread,bogus"}, want: "bogus"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			code := Run(context.Background(), tc.args, deps)
			if code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if !strings.Contains(errOut.String(), tc.want) {
				t.Fatalf("stderr=%q, want %q", errOut.String(), tc.want)
			}
		})
	}
	if calls != 0 {
		t.Fatalf("calls=%d, want none", calls)
	}
}

func TestIssue1054SlackThreadViewSurfacesTheServerErrorCode(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusConflict, `{"success":false,"data":null,"error":"Direct message history is not readable through the channel-thread read.","code":"dm_not_supported","details":null}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "operator-token"})

	code := Run(context.Background(), []string{
		"slack", "thread", "view", "--project", "proj", "--session", "session-1",
	}, deps)

	if code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(errOut.String(), "[dm_not_supported]") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestIssue1054SlackThreadViewHelpRendersTheNestedAction(t *testing.T) {
	deps, out, errOut := testDeps(nil, map[string]string{})
	for _, args := range [][]string{
		{"slack", "thread", "--help"},
		{"slack", "thread", "view", "--help"},
	} {
		code := Run(context.Background(), args, deps)
		if code != ExitOK {
			t.Fatalf("args=%v code=%d stderr=%q", args, code, errOut.String())
		}
		if !strings.Contains(out.String(), "mo slack thread view") || !strings.Contains(out.String(), "continuation") {
			t.Fatalf("args=%v stdout=%q", args, out.String())
		}
		out.Reset()
	}
}
