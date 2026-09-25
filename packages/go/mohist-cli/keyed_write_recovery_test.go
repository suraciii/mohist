package mohistcli

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/url"
	"os/exec"
	"strings"
	"testing"
)

// failingBody is a response body that dies mid-read: the Server answered but
// the caller never learns what it decided.
type failingBody struct{}

func (failingBody) Read([]byte) (int, error) { return 0, io.ErrUnexpectedEOF }
func (failingBody) Close() error             { return nil }

func TestShellWordKeepsOneValueIntactUnderAPosixShell(t *testing.T) {
	cases := map[string]string{
		"":              "''",
		"retry-1":       "retry-1",
		"wr_01J/leaf":   "wr_01J/leaf",
		"k 1":           "'k 1'",
		"it's":          `'it'\''s'`,
		"$(echo pwned)": "'$(echo pwned)'",
		"`echo pwned`":  "'`echo pwned`'",
		"a;b":           "'a;b'",
		`"quoted"`:      "'\"quoted\"'",
		"*":             "'*'",
	}
	for value, want := range cases {
		word := shellWord(value)
		if word != want {
			t.Fatalf("shellWord(%q)=%q want %q", value, word, want)
		}
		// The real shell is the only authority on quoting: whatever it passes
		// through must be the original value, byte for byte.
		out, err := exec.Command("/bin/sh", "-c", "printf '%s' "+word).Output()
		if err != nil {
			t.Fatalf("sh -c printf %s: %v", word, err)
		}
		if string(out) != value {
			t.Fatalf("sh round-trip of %q produced %q", value, out)
		}
	}
}

func TestRunControlRecoveryHintQuotesTheCallersOwnKey(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusServiceUnavailable, `{"success":false,"error":"still executing","code":"operation_pending","effect":"unknown","retrySafe":true}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	code := Run(context.Background(), []string{"run", "retry", "wr-1", "--idempotency-key", "k 1"}, deps)

	if code != ExitOperation || !strings.Contains(errOut.String(), "hint: mo run retry wr-1 --idempotency-key 'k 1'") {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
}

func TestKeyedWriteRetriesOnceWhenTheResponseBodyIsLost(t *testing.T) {
	attempts := 0
	keys := []string{}
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		attempts++
		keys = append(keys, r.Header.Get("Idempotency-Key"))
		if attempts == 1 {
			return &http.Response{StatusCode: http.StatusOK, Body: failingBody{}, Header: make(http.Header)}, nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"workflowRunId":"wr-1"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	code := Run(context.Background(), []string{"run", "retry", "wr-1"}, deps)

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if attempts != 2 {
		t.Fatalf("attempts=%d", attempts)
	}
	if keys[0] != keys[1] {
		t.Fatalf("keys=%q", keys)
	}
	// The generated key is readable on stderr before the request, so a lost
	// response is recoverable at all.
	if !strings.Contains(errOut.String(), "Idempotency-Key: "+keys[0]) || !strings.Contains(out.String(), "wr-1") {
		t.Fatalf("stderr=%q stdout=%q", errOut.String(), out.String())
	}
}

func TestUnkeyedWriteDoesNotRepeatWhenTheResponseBodyIsLost(t *testing.T) {
	attempts := 0
	deps, _, _ := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		attempts++
		return &http.Response{StatusCode: http.StatusOK, Body: failingBody{}, Header: make(http.Header)}, nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	c, err := newClient(Config{ServerURL: "http://server/", OperatorToken: "token"}, deps.HTTPClient)
	if err != nil {
		t.Fatalf("client=%v", err)
	}

	_, err = c.requestHeaders(context.Background(), http.MethodPost, "/api/unkeyed", map[string]any{}, nil, true)

	var operation *operationError
	if !errors.As(err, &operation) {
		t.Fatalf("err=%v", err)
	}
	if attempts != 1 {
		t.Fatalf("attempts=%d: an unkeyed write has no fence to absorb a second attempt", attempts)
	}
	if operation.code != "response_error" || operation.effect != "unknown" || operation.retrySafe == nil || *operation.retrySafe {
		t.Fatalf("operation=%+v", operation)
	}
}

func TestClientTimeoutRetriesTheSameKeyThenStatesTheUnknown(t *testing.T) {
	attempts := 0
	keys := []string{}
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		attempts++
		keys = append(keys, r.Header.Get("Idempotency-Key"))
		return nil, &url.Error{Op: "Post", URL: r.URL.String(), Err: context.DeadlineExceeded}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	code := Run(context.Background(), []string{"run", "retry", "wr-1", "--idempotency-key", "timeout-key"}, deps)

	if code != ExitOperation || attempts != 2 {
		t.Fatalf("code=%d attempts=%d stderr=%q", code, attempts, errOut.String())
	}
	if keys[0] != "timeout-key" || keys[1] != "timeout-key" {
		t.Fatalf("keys=%q", keys)
	}
	if !strings.Contains(errOut.String(), "hint: mo run retry wr-1 --idempotency-key timeout-key") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestInterruptedKeyedWriteIsNotRetriedAndNamesItsRecovery(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	attempts := 0
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		attempts++
		cancel()
		return nil, r.Context().Err()
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	code := Run(ctx, []string{"run", "retry", "wr-1", "--idempotency-key", "k 1", "--json", "status"}, deps)

	if code != ExitCanceled || attempts != 1 {
		t.Fatalf("code=%d attempts=%d stderr=%q", code, attempts, errOut.String())
	}
	for _, want := range []string{
		`"code":"canceled"`,
		`"effect":"unknown"`,
		`"retrySafe":true`,
		`"nextAction":"mo run retry wr-1 --idempotency-key 'k 1'"`,
	} {
		if !strings.Contains(errOut.String(), want) {
			t.Fatalf("stderr=%q missing %s", errOut.String(), want)
		}
	}
}

func TestInterruptedReadChangedNothingAndRepeatsSafely(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		cancel()
		return nil, r.Context().Err()
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	code := Run(ctx, []string{"run", "view", "wr-1", "--json", "status"}, deps)

	if code != ExitCanceled {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	for _, want := range []string{`"code":"canceled"`, `"effect":"none"`, `"retrySafe":true`} {
		if !strings.Contains(errOut.String(), want) {
			t.Fatalf("stderr=%q missing %s", errOut.String(), want)
		}
	}
}
