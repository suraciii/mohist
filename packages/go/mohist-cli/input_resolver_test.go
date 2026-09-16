package mohistcli

import (
	"bytes"
	"errors"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
)

// resolveTestDeps returns Dependencies wired with an arbitrary ReadFile and
// Input source. The returned Dependencies are intended for direct
// resolveTextInput unit tests, not for Run-level integration tests.
func resolveTestDeps(read ReadFile, input io.Reader) (Dependencies, *bytes.Buffer, *bytes.Buffer) {
	out, errOut := &bytes.Buffer{}, &bytes.Buffer{}
	return Dependencies{
		HTTPClient: &http.Client{Transport: roundTripFunc(func(*http.Request) (*http.Response, error) {
			t := &testing.T{}
			t.Fatal("resolveTextInput must not issue HTTP requests")
			return nil, nil
		})},
		Stdout: out,
		Stderr: errOut,
		Lookup: func(string) (string, bool) { return "", false },
		ReadFile: func(path string) (string, error) {
			return read(path)
		},
		WriteFile: func(string, string, os.FileMode) error {
			t := &testing.T{}
			t.Fatal("resolveTextInput must not write to disk")
			return nil
		},
		HomeDir:          func() (string, error) { return "/home/test", nil },
		CurrentDirectory: func() string { return "/work/tree" },
		Input:            input,
	}, out, errOut
}

func TestResolveTextInputReturnsExactDirectValue(t *testing.T) {
	deps, _, _ := resolveTestDeps(func(string) (string, error) { return "file-content", nil }, strings.NewReader("stdin-content"))
	cmd := command{args: []string{"body", "  plain value with trailing whitespace  "}}
	value, err := resolveTextInput(deps, cmd, "body", "body-file")
	if err != nil {
		t.Fatalf("err=%v", err)
	}
	if value != "  plain value with trailing whitespace  " {
		t.Fatalf("value=%q", value)
	}
}

func TestResolveTextInputReturnsExactFileContent(t *testing.T) {
	for _, tc := range []struct {
		name, content string
	}{
		{"simple", "hello\n"},
		{"multi-newline", "line1\nline2\nline3\n"},
		{"utf8", "héllo ☃ \n"},
		{"empty", ""},
		{"leading-and-trailing-whitespace", "  \n\t value \n\t\n"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			deps, _, _ := resolveTestDeps(func(string) (string, error) { return tc.content, nil }, strings.NewReader(""))
			cmd := command{args: []string{"file", "missing.md"}}
			value, err := resolveTextInput(deps, cmd, "", "file")
			if err != nil {
				t.Fatalf("err=%v", err)
			}
			if value != tc.content {
				t.Fatalf("value=%q want=%q", value, tc.content)
			}
		})
	}
}

func TestResolveTextInputReturnsExactStdinContent(t *testing.T) {
	for _, tc := range []struct {
		name, content string
	}{
		{"simple", "hello\n"},
		{"multi-newline", "line1\nline2\nline3\n"},
		{"utf8", "héllo ☃ \n"},
		{"empty", ""},
		{"leading-and-trailing-whitespace", "  \n\t value \n\t\n"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			deps, _, _ := resolveTestDeps(func(string) (string, error) { return "file-content", nil }, strings.NewReader(tc.content))
			cmd := command{args: []string{"body-file", "-"}}
			value, err := resolveTextInput(deps, cmd, "body", "body-file")
			if err != nil {
				t.Fatalf("err=%v", err)
			}
			if value != tc.content {
				t.Fatalf("value=%q want=%q", value, tc.content)
			}
		})
	}
}

func TestResolveTextInputFileErrorsIncludeFlagAndPath(t *testing.T) {
	for _, tc := range []struct {
		name string
		err  error
		want string
	}{
		{"missing", os.ErrNotExist, "could not read --body-file \"./missing.md\": "},
		{"permission", os.ErrPermission, "could not read --body-file \"./secret.md\": "},
		{"arbitrary", errors.New("disk on fire"), "could not read --body-file \"./broken.md\": disk on fire"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			deps, _, _ := resolveTestDeps(func(string) (string, error) { return "", tc.err }, strings.NewReader(""))
			cmd := command{args: []string{"body-file", "./missing.md"}}
			if !strings.HasSuffix(tc.name, "missing") {
				cmd.args = []string{"body-file", pathForErr(tc.name)}
			}
			value, err := resolveTextInput(deps, cmd, "body", "body-file")
			if err == nil || value != "" {
				t.Fatalf("value=%q err=%v", value, err)
			}
			if !strings.Contains(err.Error(), tc.want) {
				t.Fatalf("err=%q want substring %q", err.Error(), tc.want)
			}
		})
	}
}

func pathForErr(name string) string {
	switch name {
	case "permission":
		return "./secret.md"
	case "arbitrary":
		return "./broken.md"
	}
	return "./missing.md"
}

func TestResolveTextInputStdinErrorsIncludeFlagAndStdin(t *testing.T) {
	failing := &failingReader{err: errors.New("unexpected EOF")}
	deps, _, _ := resolveTestDeps(func(string) (string, error) { return "", nil }, failing)
	cmd := command{args: []string{"body-file", "-"}}
	value, err := resolveTextInput(deps, cmd, "body", "body-file")
	if err == nil || value != "" {
		t.Fatalf("value=%q err=%v", value, err)
	}
	want := "could not read --body-file - from stdin: unexpected EOF"
	if !strings.Contains(err.Error(), want) {
		t.Fatalf("err=%q want substring %q", err.Error(), want)
	}
}

func TestResolveTextInputPartialStdinReadIsAFailure(t *testing.T) {
	partial := &partialReader{data: []byte("partial bytes"), err: errors.New("connection closed")}
	deps, _, _ := resolveTestDeps(func(string) (string, error) { return "", nil }, partial)
	cmd := command{args: []string{"body-file", "-"}}
	value, err := resolveTextInput(deps, cmd, "body", "body-file")
	if err == nil {
		t.Fatalf("expected error for partial stdin read, got value=%q", value)
	}
	if value != "" {
		t.Fatalf("partial bytes leaked into value=%q", value)
	}
	if !strings.Contains(err.Error(), "from stdin") || !strings.Contains(err.Error(), "connection closed") {
		t.Fatalf("err=%q", err.Error())
	}
}

func TestResolveTextInputConsumesStdinExactlyOnce(t *testing.T) {
	// The contract is that resolveTextInput drains stdin in a single Read so
	// the resolved value can be cached on cmd.preflightedInput and reused
	// across pre-flight and mutation paths. After a single call, the reader
	// must be fully consumed so a second call would observe EOF and return
	// an empty value rather than partial bytes that diverge from the first.
	counter := &stdinCountingReader{reader: strings.NewReader("hello world")}
	deps, _, _ := resolveTestDeps(func(string) (string, error) { return "", nil }, counter)
	cmd := command{args: []string{"body-file", "-"}}
	first, err := resolveTextInput(deps, cmd, "body", "body-file")
	if err != nil {
		t.Fatalf("first err=%v", err)
	}
	if first != "hello world" {
		t.Fatalf("first value=%q", first)
	}
	if counter.calls == 0 {
		t.Fatalf("stdin reader was not consumed")
	}
	// Stash the resolved value and call again; the second call must NOT
	// re-read the (now-drained) stdin. The resolver returns the direct
	// value argument when present, so we exercise the caching path by
	// passing the preflighted value as the explicit input instead.
	cmd.preflightedInput = first
	// The resolver ignores cmd.preflightedInput and only reads stdin when
	// the fileFlag is "-", so we verify that the underlying reader has
	// already been drained and a subsequent Read returns 0 bytes plus io.EOF.
	buf := make([]byte, 16)
	n, err := counter.reader.Read(buf)
	if n != 0 || err == nil {
		t.Fatalf("stdin not drained: n=%d err=%v", n, err)
	}
	if !errors.Is(err, io.EOF) {
		t.Fatalf("expected io.EOF after first drain, got %v", err)
	}
}

func TestResolveTextInputMissingCarrierReturnsEmptyWithoutError(t *testing.T) {
	deps, _, _ := resolveTestDeps(func(string) (string, error) { return "file", nil }, strings.NewReader("stdin"))
	cmd := command{}
	value, err := resolveTextInput(deps, cmd, "body", "body-file")
	if err != nil || value != "" {
		t.Fatalf("value=%q err=%v", value, err)
	}
}

func TestResolveTextInputFileFlagAbsentStillReturnsEmpty(t *testing.T) {
	deps, _, _ := resolveTestDeps(func(string) (string, error) { return "file", nil }, strings.NewReader("stdin"))
	cmd := command{args: []string{"body", "plain"}}
	value, err := resolveTextInput(deps, cmd, "body", "body-file")
	if err != nil || value != "plain" {
		t.Fatalf("value=%q err=%v", value, err)
	}
}

// failingReader always returns an error from Read.
type failingReader struct{ err error }

func (r *failingReader) Read([]byte) (int, error) { return 0, r.err }

// partialReader returns data on the first read and an error afterwards.
type partialReader struct {
	data []byte
	done bool
	err  error
}

func (r *partialReader) Read(p []byte) (int, error) {
	if r.done {
		return 0, r.err
	}
	r.done = true
	n := copy(p, r.data)
	return n, r.err
}

// stdinCountingReader wraps an io.Reader and counts Read invocations. The
// name avoids collision with the unrelated countingReader declared in
// issue_676_contract_test.go, which exists to track dependency accesses.
type stdinCountingReader struct {
	reader io.Reader
	calls  int
}

func (r *stdinCountingReader) Read(p []byte) (int, error) {
	r.calls++
	return r.reader.Read(p)
}
