package mohistcli

import (
	"context"
	"errors"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
)

// commandFixture wires a Dependencies object that records ReadFile and
// WriteFile paths so tests can prove the implicit Project fixture was not
// consulted after a carrier failure. The transport fails any HTTP call so
// the table-driven test cannot accidentally pass when an unexpected lookup
// occurs.
type commandFixture struct {
	deps         Dependencies
	stdout       *strings.Builder
	stderr       *strings.Builder
	transport    *transportTrap
	readPaths    *pathRecorder
	writtenPaths *pathRecorder
	input        *stdinCountingReader
}

func newCommandFixture(stateFiles map[string]string) *commandFixture {
	readPaths := &pathRecorder{}
	writtenPaths := &pathRecorder{}
	trap := &transportTrap{}
	readFile := func(path string) (string, error) {
		readPaths.add(path)
		if v, ok := stateFiles[path]; ok {
			return v, nil
		}
		return "", os.ErrNotExist
	}
	stdout := &strings.Builder{}
	stderr := &strings.Builder{}
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: trap},
		Stdout:     stdout,
		Stderr:     stderr,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_TOKEN" {
				return "token", true
			}
			if name == "MOHIST_SERVER_URL" {
				return "http://server", true
			}
			return "", false
		},
		ReadFile:          readFile,
		WriteFile:         func(path, value string, _ os.FileMode) error { writtenPaths.add(path); return nil },
		HomeDir:           func() (string, error) { return "/home/test", nil },
		CurrentDirectory:  func() string { return "/work/tree" },
		OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
		ManagedPathExists: func(string) bool { return false },
	}
	input := &stdinCountingReader{reader: strings.NewReader("stdin content\n")}
	deps.Input = input
	return &commandFixture{
		deps:         deps,
		stdout:       stdout,
		stderr:       stderr,
		transport:    trap,
		readPaths:    readPaths,
		writtenPaths: writtenPaths,
		input:        input,
	}
}

// transportTrap fails any HTTP call so the table-driven test cannot
// accidentally pass when an unexpected lookup occurs. The trap counts the
// attempts so a test can assert that no HTTP request was issued.
type transportTrap struct{ calls int }

func (t *transportTrap) RoundTrip(*http.Request) (*http.Response, error) {
	t.calls++
	return nil, errors.New("transport must not be used after carrier failure")
}

// pathRecorder collects ReadFile / WriteFile paths so tests can prove no
// cli-state.json was consulted after a carrier failure.
type pathRecorder struct {
	mu    *int
	paths []string
}

func (r *pathRecorder) add(path string) {
	r.paths = append(r.paths, path)
}

func (r *pathRecorder) has(prefix string) bool {
	for _, p := range r.paths {
		if strings.Contains(p, prefix) {
			return true
		}
	}
	return false
}

func (r *pathRecorder) contains(path string) bool {
	for _, p := range r.paths {
		if p == path {
			return true
		}
	}
	return false
}

// carrierFailureCase defines one row of the cross-family carrier-failure
// table. The transport trap ensures the test cannot pass if an HTTP request
// is issued after a carrier failure.
type carrierFailureCase struct {
	name        string
	args        []string
	stateFiles  map[string]string
	fileError   error
	inputReader io.Reader
	wantErrSub  string
	wantFlag    string
}

func runCarrierFailureCase(t *testing.T, tc carrierFailureCase) {
	t.Helper()
	f := newCommandFixture(tc.stateFiles)
	if tc.fileError != nil {
		// Override the read function with one that always fails on the
		// specified path; fixture state file reads return the configured
		// content so the implicit Project can resolve when present.
		readPaths := f.readPaths
		f.deps.ReadFile = func(path string) (string, error) {
			readPaths.add(path)
			if v, ok := tc.stateFiles[path]; ok {
				return v, nil
			}
			return "", tc.fileError
		}
	}
	if tc.inputReader != nil {
		f.deps.Input = tc.inputReader
	}
	code := Run(context.Background(), tc.args, f.deps)
	if code != ExitUsage {
		t.Fatalf("%s: code=%d stderr=%q stdout=%q", tc.name, code, f.stderr.String(), f.stdout.String())
	}
	if f.transport.calls != 0 {
		t.Fatalf("%s: transport calls=%d expected 0", tc.name, f.transport.calls)
	}
	if f.writtenPaths.has(".mohist/cli-state.json") || len(f.writtenPaths.paths) != 0 {
		t.Fatalf("%s: writes=%v expected none", tc.name, f.writtenPaths.paths)
	}
	if !strings.Contains(f.stderr.String(), tc.wantFlag) {
		t.Fatalf("%s: stderr=%q want flag %q", tc.name, f.stderr.String(), tc.wantFlag)
	}
	if !strings.Contains(f.stderr.String(), tc.wantErrSub) {
		t.Fatalf("%s: stderr=%q want substring %q", tc.name, f.stderr.String(), tc.wantErrSub)
	}
}

// TestCarrierFailureIssueCreate exercises the cross-family table for Issue
// create/edit/comment. Each failure type returns ExitUsage=2 with zero HTTP
// and zero WriteFile calls. Both explicit --project and the implicit Project
// path (state file only) are exercised.
func TestCarrierFailureIssueCreateEditComment(t *testing.T) {
	stateImplicit := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	stateHome := map[string]string{"/home/test/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}

	flagSub := "could not read --body-file"
	flagStdInSub := "could not read --body-file - from stdin"

	cases := []carrierFailureCase{
		{
			name:       "issue-create-explicit-missing-file",
			args:       []string{"issue", "create", "Title", "--project", "proj", "--body-file", "./missing.md"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-create-explicit-permission",
			args:       []string{"issue", "create", "Title", "--project", "proj", "--body-file", "./secret.md"},
			fileError:  os.ErrPermission,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-create-explicit-arbitrary",
			args:       []string{"issue", "create", "Title", "--project", "proj", "--body-file", "./broken.md"},
			fileError:  errors.New("disk on fire"),
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:        "issue-create-explicit-partial-stdin",
			args:        []string{"issue", "create", "Title", "--project", "proj", "--body-file", "-"},
			inputReader: &partialReader{data: []byte("partial"), err: errors.New("connection closed")},
			wantFlag:    "--body-file",
			wantErrSub:  flagStdInSub,
		},
		{
			name:       "issue-create-implicit-missing-file",
			args:       []string{"issue", "create", "Title", "--body-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:        "issue-create-implicit-failing-stdin",
			args:        []string{"issue", "create", "Title", "--body-file", "-"},
			stateFiles:  stateImplicit,
			inputReader: &failingReader{err: errors.New("unexpected EOF")},
			wantFlag:    "--body-file",
			wantErrSub:  flagStdInSub,
		},
		{
			name:       "issue-edit-explicit-missing",
			args:       []string{"issue", "edit", "42", "--project", "proj", "--body-file", "./missing.md"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-edit-implicit-missing",
			args:       []string{"issue", "edit", "42", "--body-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-edit-with-labels-explicit-skips-preflight",
			args:       []string{"issue", "edit", "42", "--project", "proj", "--label", "bug", "--body-file", "./missing.md"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-edit-with-labels-implicit-skips-preflight",
			args:       []string{"issue", "edit", "42", "--label", "bug", "--body-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-comment-explicit-missing",
			args:       []string{"issue", "comment", "create", "42", "--project", "proj", "--body-file", "./missing.md"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-comment-implicit-missing",
			args:       []string{"issue", "comment", "create", "42", "--body-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
		{
			name:       "issue-comment-home-implicit-missing",
			args:       []string{"issue", "comment", "create", "42", "--body-file", "./missing.md"},
			stateFiles: stateHome,
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: flagSub,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			runCarrierFailureCase(t, tc)
		})
	}
}

func TestCarrierFailureEpicCreateEdit(t *testing.T) {
	stateImplicit := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	flagSub := "could not read --description-file"

	cases := []carrierFailureCase{
		{
			name:       "epic-create-explicit-missing",
			args:       []string{"epic", "create", "Title", "--project", "proj", "--description-file", "./missing.md"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--description-file",
			wantErrSub: flagSub,
		},
		{
			name:       "epic-create-implicit-missing",
			args:       []string{"epic", "create", "Title", "--description-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--description-file",
			wantErrSub: flagSub,
		},
		{
			name:        "epic-create-explicit-failing-stdin",
			args:        []string{"epic", "create", "Title", "--project", "proj", "--description-file", "-"},
			inputReader: &failingReader{err: errors.New("unexpected EOF")},
			wantFlag:    "--description-file",
			wantErrSub:  "could not read --description-file - from stdin: unexpected EOF",
		},
		{
			name:       "epic-edit-explicit-missing",
			args:       []string{"epic", "edit", "1", "--project", "proj", "--description-file", "./missing.md"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--description-file",
			wantErrSub: flagSub,
		},
		{
			name:       "epic-edit-implicit-missing",
			args:       []string{"epic", "edit", "1", "--description-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--description-file",
			wantErrSub: flagSub,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			runCarrierFailureCase(t, tc)
		})
	}
}

func TestCarrierFailureWorkflowCreateEditValidate(t *testing.T) {
	stateImplicit := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	flagSub := "could not read --file"

	cases := []carrierFailureCase{
		{
			name:       "workflow-create-explicit-missing",
			args:       []string{"workflow", "create", "--project", "proj", "--file", "./missing.yaml"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--file",
			wantErrSub: flagSub,
		},
		{
			name:       "workflow-create-implicit-missing",
			args:       []string{"workflow", "create", "--file", "./missing.yaml"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--file",
			wantErrSub: flagSub,
		},
		{
			name:        "workflow-create-implicit-failing-stdin",
			args:        []string{"workflow", "create", "--file", "-"},
			stateFiles:  stateImplicit,
			inputReader: &failingReader{err: errors.New("unexpected EOF")},
			wantFlag:    "--file",
			wantErrSub:  "could not read --file - from stdin: unexpected EOF",
		},
		{
			name:       "workflow-edit-explicit-missing",
			args:       []string{"workflow", "edit", "p-1", "--project", "proj", "--file", "./missing.yaml"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--file",
			wantErrSub: flagSub,
		},
		{
			name:       "workflow-edit-implicit-missing",
			args:       []string{"workflow", "edit", "p-1", "--file", "./missing.yaml"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--file",
			wantErrSub: flagSub,
		},
		{
			name:       "workflow-validate-missing",
			args:       []string{"workflow", "validate", "--file", "./missing.yaml"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--file",
			wantErrSub: flagSub,
		},
		{
			name:        "workflow-validate-failing-stdin",
			args:        []string{"workflow", "validate", "--file", "-"},
			inputReader: &failingReader{err: errors.New("unexpected EOF")},
			wantFlag:    "--file",
			wantErrSub:  "could not read --file - from stdin: unexpected EOF",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			runCarrierFailureCase(t, tc)
		})
	}
}

func TestCarrierFailureProjectCreateAndVerification(t *testing.T) {
	flagSub := "could not read --verification-command-file"
	cases := []carrierFailureCase{
		{
			name:       "project-create-missing",
			args:       []string{"project", "create", "p", "--path", "/tmp/p", "--verification-command-file", "./missing.sh"},
			fileError:  os.ErrNotExist,
			wantFlag:   "--verification-command-file",
			wantErrSub: flagSub,
		},
		{
			name:       "project-create-permission",
			args:       []string{"project", "create", "p", "--path", "/tmp/p", "--verification-command-file", "./secret.sh"},
			fileError:  os.ErrPermission,
			wantFlag:   "--verification-command-file",
			wantErrSub: flagSub,
		},
		{
			name:        "project-create-failing-stdin",
			args:        []string{"project", "create", "p", "--path", "/tmp/p", "--verification-command-file", "-"},
			inputReader: &failingReader{err: errors.New("unexpected EOF")},
			wantFlag:    "--verification-command-file",
			wantErrSub:  "could not read --verification-command-file - from stdin: unexpected EOF",
		},
		{
			name:       "project-verification-set-explicit-missing",
			args:       []string{"project", "workflow", "verification", "set", "--project", "proj", "--command-file", "./missing.sh"},
			stateFiles: map[string]string{},
			fileError:  os.ErrNotExist,
			wantFlag:   "--command-file",
			wantErrSub: "could not read --command-file",
		},
		{
			name:       "project-verification-set-implicit-missing",
			args:       []string{"project", "workflow", "verification", "set", "--command-file", "./missing.sh"},
			stateFiles: map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`},
			fileError:  os.ErrNotExist,
			wantFlag:   "--command-file",
			wantErrSub: "could not read --command-file",
		},
		{
			name:       "project-prompt-set-explicit-missing",
			args:       []string{"project", "workflow", "prompt", "set", "intro", "--project", "proj", "--body-file", "./missing.md"},
			stateFiles: map[string]string{},
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: "could not read --body-file",
		},
		{
			name:       "project-prompt-set-implicit-missing",
			args:       []string{"project", "workflow", "prompt", "set", "intro", "--body-file", "./missing.md"},
			stateFiles: map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`},
			fileError:  os.ErrNotExist,
			wantFlag:   "--body-file",
			wantErrSub: "could not read --body-file",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			runCarrierFailureCase(t, tc)
		})
	}
}

func TestCarrierFailureAgentStartLaunchAndSessionFollowup(t *testing.T) {
	stateImplicit := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	cases := []carrierFailureCase{
		{
			name:       "agent-start-explicit-missing",
			args:       []string{"agent", "start", "--project", "proj", "--prompt-file", "./missing.md"},
			stateFiles: map[string]string{},
			fileError:  os.ErrNotExist,
			wantFlag:   "--prompt-file",
			wantErrSub: "could not read --prompt-file",
		},
		{
			name:       "agent-start-implicit-missing",
			args:       []string{"agent", "start", "--prompt-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--prompt-file",
			wantErrSub: "could not read --prompt-file",
		},
		{
			name:       "agent-launch-explicit-missing",
			args:       []string{"agent", "launch", "agent_1", "--project", "proj", "--prompt-file", "./missing.md"},
			stateFiles: map[string]string{},
			fileError:  os.ErrNotExist,
			wantFlag:   "--prompt-file",
			wantErrSub: "could not read --prompt-file",
		},
		{
			name:       "agent-launch-implicit-missing",
			args:       []string{"agent", "launch", "agent_1", "--prompt-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--prompt-file",
			wantErrSub: "could not read --prompt-file",
		},
		{
			name:        "agent-launch-implicit-failing-stdin",
			args:        []string{"agent", "launch", "agent_1", "--prompt-file", "-"},
			stateFiles:  stateImplicit,
			inputReader: &failingReader{err: errors.New("unexpected EOF")},
			wantFlag:    "--prompt-file",
			wantErrSub:  "could not read --prompt-file - from stdin: unexpected EOF",
		},
		{
			name:       "session-followup-explicit-missing",
			args:       []string{"session", "followup", "sess-1", "--project", "proj", "--text-file", "./missing.md"},
			stateFiles: map[string]string{},
			fileError:  os.ErrNotExist,
			wantFlag:   "--text-file",
			wantErrSub: "could not read --text-file",
		},
		{
			name:       "session-followup-implicit-missing",
			args:       []string{"session", "followup", "sess-1", "--text-file", "./missing.md"},
			stateFiles: stateImplicit,
			fileError:  os.ErrNotExist,
			wantFlag:   "--text-file",
			wantErrSub: "could not read --text-file",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			runCarrierFailureCase(t, tc)
		})
	}
}

// TestCarrierFailureSkipsProjectStateLookup confirms that the implicit
// Project state file is not consulted after a carrier failure: a failing
// --body-file must stop the command before resolveProject reads
// cli-state.json. A succeeding state read should never happen because the
// transport trap also forbids any Project-scoped lookup.
func TestCarrierFailureSkipsProjectStateLookup(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	f := newCommandFixture(state)
	f.deps.ReadFile = func(path string) (string, error) {
		f.readPaths.add(path)
		if v, ok := state[path]; ok {
			return v, nil
		}
		return "", os.ErrNotExist
	}
	code := Run(context.Background(), []string{"issue", "edit", "42", "--body-file", "./missing.md"}, f.deps)
	if code != ExitUsage {
		t.Fatalf("code=%d stderr=%q", code, f.stderr.String())
	}
	if f.readPaths.contains("/work/tree/.mohist/cli-state.json") {
		t.Fatalf("implicit Project state file was read after carrier failure: %v", f.readPaths.paths)
	}
	if f.transport.calls != 0 {
		t.Fatalf("transport calls=%d after carrier failure", f.transport.calls)
	}
	if !strings.Contains(f.stderr.String(), "could not read --body-file") {
		t.Fatalf("stderr=%q", f.stderr.String())
	}
}

// TestIssueEditWithLabelsStdinOnceOnly confirms that the pre-flight GET
// inside issueEditWithLabels is skipped on a stdin carrier failure and the
// stdin reader is consumed at most once for a successful stdin payload.
func TestIssueEditWithLabelsSkipsPreflightOnStdinFailure(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	f := newCommandFixture(state)
	f.deps.Input = &failingReader{err: errors.New("unexpected EOF")}
	code := Run(context.Background(), []string{"issue", "edit", "42", "--label", "bug", "--body-file", "-"}, f.deps)
	if code != ExitUsage {
		t.Fatalf("code=%d stderr=%q", code, f.stderr.String())
	}
	if f.transport.calls != 0 {
		t.Fatalf("pre-flight GET was issued: %d", f.transport.calls)
	}
	if !strings.Contains(f.stderr.String(), "could not read --body-file - from stdin") {
		t.Fatalf("stderr=%q", f.stderr.String())
	}
}

func TestIssueEditWithLabelsStdinConsumedOnceOnSuccess(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	f := newCommandFixture(state)
	var patchBody string
	patched := 0
	read := false
	reader := &stdinCountingReader{reader: strings.NewReader("hello world\n")}
	f.deps.Input = reader
	// Replace the transport to admit the pre-flight GET and the PATCH but
	// reject any other call.
	f.deps.HTTPClient = &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch r.Method {
		case http.MethodGet:
			if r.URL.Path != "/api/projects/proj/issues/42" {
				t.Fatalf("unexpected GET path=%s", r.URL.Path)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":42,"labels":{"bug":""}}}`), nil
		case http.MethodPatch:
			patched++
			body, _ := io.ReadAll(r.Body)
			patchBody = string(body)
			return response(http.StatusOK, `{"success":true,"data":{"number":42}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	})}
	_ = read
	code := Run(context.Background(), []string{"issue", "edit", "42", "--label", "bug", "--body-file", "-"}, f.deps)
	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, f.stderr.String())
	}
	if patched != 1 {
		t.Fatalf("PATCH count=%d expected 1", patched)
	}
	if !strings.Contains(patchBody, `"body":"hello world\n"`) {
		t.Fatalf("body=%s", patchBody)
	}
	// The transport admitted one GET (pre-flight) and one PATCH (mutation);
	// stdin must have been consumed exactly once and fully drained so a
	// second resolver call would observe EOF and return "". This guards
	// against an accidental second read that would split the body across
	// pre-flight and mutation paths.
	if reader.calls == 0 {
		t.Fatalf("stdin was not consumed")
	}
	if leftover, err := io.ReadAll(reader.reader); err != nil || len(leftover) != 0 {
		t.Fatalf("stdin not drained: leftover=%q err=%v", leftover, err)
	}
}

// TestRequestBodyIdentityAcrossFileAndStdin asserts that file and stdin
// with the same UTF-8 content produce identical request payloads. The
// variants cover empty content, multiple trailing newlines, UTF-8
// without a trailing newline, and whitespace-only content so that
// carrier-specific normalization such as file-only newline trimming is
// observed to fail rather than pass. Each variant runs through the full
// Run pipeline with both --body-file ./path and --body-file -, the
// transport capturing the exact JSON body for comparison.
func TestRequestBodyIdentityAcrossFileAndStdin(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	variants := []struct {
		name, body, bodyJSON string
	}{
		{
			name:     "utf8-with-trailing-newline",
			body:     "héllo\nworld\n",
			bodyJSON: `"body":"héllo\nworld\n"`,
		},
		{
			name:     "empty-content",
			body:     "",
			bodyJSON: `"body":""`,
		},
		{
			name:     "multiple-trailing-newlines",
			body:     "hello\n\n\n",
			bodyJSON: `"body":"hello\n\n\n"`,
		},
		{
			name:     "utf8-without-trailing-newline",
			body:     "héllo ☃",
			bodyJSON: `"body":"héllo ☃"`,
		},
		{
			name:     "leading-and-trailing-whitespace",
			body:     "  \n\t value \n\t\n",
			bodyJSON: `"body":"  \n\t value \n\t\n"`,
		},
		{
			name:     "only-newlines",
			body:     "\n\n\n",
			bodyJSON: `"body":"\n\n\n"`,
		},
		{
			name:     "only-whitespace",
			body:     "   \t  ",
			bodyJSON: `"body":"   \t  "`,
		},
	}
	for _, variant := range variants {
		t.Run(variant.name, func(t *testing.T) {
			body := variant.body
			var fileBody, stdinBody string
			var fileRead, stdinRead bool
			read := func(path string) (string, error) {
				if path == "./body.md" {
					fileRead = true
					return body, nil
				}
				if v, ok := state[path]; ok {
					return v, nil
				}
				return "", os.ErrNotExist
			}
			calls := 0
			transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
				b, _ := io.ReadAll(r.Body)
				calls++
				switch calls {
				case 1:
					fileBody = string(b)
				case 2:
					stdinBody = string(b)
				}
				return response(http.StatusOK, `{"success":true,"data":{"number":1}}`), nil
			})
			makeDeps := func() Dependencies {
				return Dependencies{
					HTTPClient: &http.Client{Transport: transport},
					Stdout:     &strings.Builder{},
					Stderr:     &strings.Builder{},
					Lookup: func(name string) (string, bool) {
						if name == "MOHIST_TOKEN" {
							return "token", true
						}
						if name == "MOHIST_SERVER_URL" {
							return "http://server", true
						}
						return "", false
					},
					ReadFile:          read,
					HomeDir:           func() (string, error) { return "/home/test", nil },
					CurrentDirectory:  func() string { return "/work/tree" },
					Input:             &stdinCountingReader{reader: strings.NewReader(body)},
					OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
					ManagedPathExists: func(string) bool { return false },
				}
			}
			fileDeps := makeDeps()
			if code := Run(context.Background(), []string{"issue", "create", "Title", "--body-file", "./body.md"}, fileDeps); code != ExitOK {
				t.Fatalf("file code=%d", code)
			}
			stdinDeps := makeDeps()
			counter := &stdinCountingReader{reader: strings.NewReader(body)}
			stdinDeps.Input = counter
			// Replace ReadFile so stdin path is used: any non-state read
			// returns an error so file reads are never confused with stdin
			// reads.
			stdinDeps.ReadFile = func(path string) (string, error) {
				if v, ok := state[path]; ok {
					return v, nil
				}
				return "", os.ErrNotExist
			}
			if code := Run(context.Background(), []string{"issue", "create", "Title", "--body-file", "-"}, stdinDeps); code != ExitOK {
				t.Fatalf("stdin code=%d", code)
			}
			stdinRead = counter.calls > 0
			if !fileRead {
				t.Fatalf("file was never read")
			}
			if !stdinRead {
				t.Fatalf("stdin was never consumed")
			}
			if fileBody == "" || stdinBody == "" {
				t.Fatalf("file=%q stdin=%q", fileBody, stdinBody)
			}
			if fileBody != stdinBody {
				t.Fatalf("file=%q stdin=%q", fileBody, stdinBody)
			}
			if !strings.Contains(fileBody, variant.bodyJSON) {
				t.Fatalf("body did not preserve exact bytes for %q: %s", variant.name, fileBody)
			}
		})
	}
}

// TestEmptyCarriersForClearableCommands ensures Issue body, Issue comment,
// and Epic description can still send explicit empty strings. Issue and
// Epic edit must accept --body "" / --description "" as a clear operation;
// the empty carrier after a successful read is sent as "" rather than
// being treated as a read failure.
func TestEmptyCarriersForClearableCommands(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	read := func(path string) (string, error) {
		if path == "./body.md" {
			return "", nil
		}
		if v, ok := state[path]; ok {
			return v, nil
		}
		return "", os.ErrNotExist
	}
	transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
		body, _ := io.ReadAll(r.Body)
		switch r.URL.Path {
		case "/api/projects/proj/issues":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue create body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1}}`), nil
		case "/api/projects/proj/issues/1":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue edit body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1}}`), nil
		case "/api/projects/proj/issues/1/comments":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue comment body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{}}`), nil
		case "/api/projects/proj/epics/":
			if !strings.Contains(string(body), `"description":""`) {
				t.Fatalf("epic create body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1}}`), nil
		case "/api/projects/proj/epics/1":
			if !strings.Contains(string(body), `"description":""`) {
				t.Fatalf("epic edit body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{}}`), nil
	})
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: transport},
		Stdout:     &strings.Builder{},
		Stderr:     &strings.Builder{},
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_TOKEN" {
				return "token", true
			}
			if name == "MOHIST_SERVER_URL" {
				return "http://server", true
			}
			return "", false
		},
		ReadFile:          read,
		HomeDir:           func() (string, error) { return "/home/test", nil },
		CurrentDirectory:  func() string { return "/work/tree" },
		Input:             strings.NewReader(""),
		OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
		ManagedPathExists: func(string) bool { return false },
	}
	cases := [][]string{
		{"issue", "create", "Title", "--body-file", "./body.md"},
		{"issue", "edit", "1", "--body-file", "./body.md"},
		{"issue", "comment", "create", "1", "--body-file", "./body.md"},
		{"epic", "create", "Title", "--description-file", "./body.md"},
		{"epic", "edit", "1", "--description-file", "./body.md"},
	}
	for _, args := range cases {
		t.Run(args[0]+"-"+args[1], func(t *testing.T) {
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d", code)
			}
		})
	}
}

// TestBlankRequiredCommandsRejectWithoutHTTP ensures verification commands,
// Project Prompt bodies, Workflow Definitions, Agent prompts, and Session
// follow-ups reject blank values locally without issuing any HTTP request.
func TestBlankRequiredCommandsRejectWithoutHTTP(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	cases := []struct {
		name string
		args []string
		want string
	}{
		{
			name: "workflow-blank",
			args: []string{"workflow", "create", "--file", "./blank.yaml"},
			want: "--file must not be blank",
		},
		{
			name: "project-verification-blank",
			args: []string{"project", "workflow", "verification", "set", "--command-file", "./blank.sh"},
			want: "--command must not be blank",
		},
		{
			name: "project-prompt-blank",
			args: []string{"project", "workflow", "prompt", "set", "intro", "--body-file", "./blank.md"},
			want: "--body must not be blank",
		},
		{
			name: "agent-start-blank",
			args: []string{"agent", "start", "--prompt-file", "./blank.md"},
			want: "--prompt must not be blank",
		},
		{
			name: "agent-launch-blank",
			args: []string{"agent", "launch", "agent_1", "--prompt-file", "./blank.md"},
			want: "--prompt must not be blank",
		},
		{
			name: "session-followup-blank",
			args: []string{"session", "followup", "sess-1", "--text-file", "./blank.md"},
			want: "--text must not be blank",
		},
		{
			name: "project-create-blank-verification",
			args: []string{"project", "create", "p", "--path", "/tmp/p", "--verification-command-file", "./blank.sh"},
			want: "--verification-command must not be blank",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newCommandFixture(state)
			f.deps.ReadFile = func(path string) (string, error) {
				f.readPaths.add(path)
				if v, ok := state[path]; ok {
					return v, nil
				}
				return "", nil
			}
			code := Run(context.Background(), tc.args, f.deps)
			if code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, f.stderr.String())
			}
			if f.transport.calls != 0 {
				t.Fatalf("HTTP calls=%d", f.transport.calls)
			}
			if len(f.writtenPaths.paths) != 0 {
				t.Fatalf("writes=%v", f.writtenPaths.paths)
			}
			if !strings.Contains(f.stderr.String(), tc.want) {
				t.Fatalf("stderr=%q want=%q", f.stderr.String(), tc.want)
			}
		})
	}
}

// TestRequestBodyIdentityAcrossCarriersForAllClearableCommands extends
// the file/stdin identity guarantee to every clearable command family.
// Each family member must produce an identical JSON body for the same
// UTF-8 input across the file and stdin carriers so carriers are not
// observably different to the Server.
func TestRequestBodyIdentityAcrossCarriersForAllClearableCommands(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	body := "line one\nline two\nline three\n"
	cases := []struct {
		name      string
		args      []string
		wantPath  string
		wantField string
	}{
		{
			name:      "issue-create",
			args:      []string{"issue", "create", "Title"},
			wantPath:  "/api/projects/proj/issues",
			wantField: "body",
		},
		{
			name:      "issue-edit",
			args:      []string{"issue", "edit", "42"},
			wantPath:  "/api/projects/proj/issues/42",
			wantField: "body",
		},
		{
			name:      "issue-comment-create",
			args:      []string{"issue", "comment", "create", "42"},
			wantPath:  "/api/projects/proj/issues/42/comments",
			wantField: "body",
		},
		{
			name:      "epic-create",
			args:      []string{"epic", "create", "Title"},
			wantPath:  "/api/projects/proj/epics/",
			wantField: "description",
		},
		{
			name:      "epic-edit",
			args:      []string{"epic", "edit", "1"},
			wantPath:  "/api/projects/proj/epics/1",
			wantField: "description",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var fileBody, stdinBody string
			calls := 0
			transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
				b, _ := io.ReadAll(r.Body)
				if r.URL.EscapedPath() != tc.wantPath {
					t.Fatalf("unexpected path %s want %s", r.URL.EscapedPath(), tc.wantPath)
				}
				calls++
				switch calls {
				case 1:
					fileBody = string(b)
				case 2:
					stdinBody = string(b)
				}
				if tc.wantField == "description" {
					return response(http.StatusOK, `{"success":true,"data":{"number":1,"description":""}}`), nil
				}
				return response(http.StatusOK, `{"success":true,"data":{"number":1,"body":""}}`), nil
			})
			makeDeps := func() Dependencies {
				return Dependencies{
					HTTPClient: &http.Client{Transport: transport},
					Stdout:     &strings.Builder{},
					Stderr:     &strings.Builder{},
					Lookup: func(name string) (string, bool) {
						if name == "MOHIST_TOKEN" {
							return "token", true
						}
						if name == "MOHIST_SERVER_URL" {
							return "http://server", true
						}
						return "", false
					},
					ReadFile: func(path string) (string, error) {
						if path == "./body.md" {
							return body, nil
						}
						if v, ok := state[path]; ok {
							return v, nil
						}
						return "", os.ErrNotExist
					},
					HomeDir:           func() (string, error) { return "/home/test", nil },
					CurrentDirectory:  func() string { return "/work/tree" },
					Input:             &stdinCountingReader{reader: strings.NewReader(body)},
					OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
					ManagedPathExists: func(string) bool { return false },
				}
			}
			fileDeps := makeDeps()
			fileArgs := append([]string{}, tc.args...)
			fileArgs = append(fileArgs, "--"+tc.wantField+"-file", "./body.md")
			if code := Run(context.Background(), fileArgs, fileDeps); code != ExitOK {
				t.Fatalf("file code=%d", code)
			}
			stdinDeps := makeDeps()
			counter := &stdinCountingReader{reader: strings.NewReader(body)}
			stdinDeps.Input = counter
			stdinDeps.ReadFile = func(path string) (string, error) {
				if v, ok := state[path]; ok {
					return v, nil
				}
				return "", os.ErrNotExist
			}
			stdinArgs := append([]string{}, tc.args...)
			stdinArgs = append(stdinArgs, "--"+tc.wantField+"-file", "-")
			if code := Run(context.Background(), stdinArgs, stdinDeps); code != ExitOK {
				t.Fatalf("stdin code=%d", code)
			}
			if counter.calls == 0 {
				t.Fatalf("stdin was never consumed")
			}
			if fileBody == "" || stdinBody == "" {
				t.Fatalf("file=%q stdin=%q", fileBody, stdinBody)
			}
			if fileBody != stdinBody {
				t.Fatalf("file=%q stdin=%q", fileBody, stdinBody)
			}
			wantSub := `"` + tc.wantField + `":"line one\nline two\nline three\n"`
			if !strings.Contains(fileBody, wantSub) {
				t.Fatalf("body did not preserve exact bytes: %s", fileBody)
			}
		})
	}
}

// TestDirectEmptyStringForClearableCommands proves that --body "" and
// --description "" are accepted as legitimate clear operations by the
// parser and the body builder. The empty string is sent verbatim, not
// coerced, and never treated as a read failure.
func TestDirectEmptyStringForClearableCommands(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
		body, _ := io.ReadAll(r.Body)
		switch {
		case r.URL.Path == "/api/projects/proj/issues":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue create body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"body":""}}`), nil
		case r.URL.Path == "/api/projects/proj/issues/1":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue edit body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"body":""}}`), nil
		case r.URL.Path == "/api/projects/proj/issues/1/comments":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue comment body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"id":1,"body":""}}`), nil
		case r.URL.Path == "/api/projects/proj/epics/":
			if !strings.Contains(string(body), `"description":""`) {
				t.Fatalf("epic create body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"description":""}}`), nil
		case r.URL.Path == "/api/projects/proj/epics/1":
			if !strings.Contains(string(body), `"description":""`) {
				t.Fatalf("epic edit body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"description":""}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{}}`), nil
	})
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: transport},
		Stdout:     &strings.Builder{},
		Stderr:     &strings.Builder{},
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_TOKEN" {
				return "token", true
			}
			if name == "MOHIST_SERVER_URL" {
				return "http://server", true
			}
			return "", false
		},
		ReadFile: func(path string) (string, error) {
			if v, ok := state[path]; ok {
				return v, nil
			}
			return "", os.ErrNotExist
		},
		HomeDir:           func() (string, error) { return "/home/test", nil },
		CurrentDirectory:  func() string { return "/work/tree" },
		Input:             strings.NewReader(""),
		OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
		ManagedPathExists: func(string) bool { return false },
	}
	cases := [][]string{
		{"issue", "create", "Title", "--body", ""},
		{"issue", "edit", "1", "--body", ""},
		{"issue", "comment", "create", "1", "--body", ""},
		{"epic", "create", "Title", "--description", ""},
		{"epic", "edit", "1", "--description", ""},
	}
	for _, args := range cases {
		t.Run(args[0]+"-"+args[1], func(t *testing.T) {
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d", code)
			}
		})
	}
}

// TestEmptyStdinForClearableCommands proves that --body-file - with empty
// stdin is treated as a successful read whose value is "". The empty
// carrier is sent verbatim as the field value rather than reclassified
// as a read failure.
func TestEmptyStdinForClearableCommands(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
		body, _ := io.ReadAll(r.Body)
		switch r.URL.Path {
		case "/api/projects/proj/issues":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue create body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"body":""}}`), nil
		case "/api/projects/proj/issues/1":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue edit body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"body":""}}`), nil
		case "/api/projects/proj/issues/1/comments":
			if !strings.Contains(string(body), `"body":""`) {
				t.Fatalf("issue comment body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"id":1,"body":""}}`), nil
		case "/api/projects/proj/epics/":
			if !strings.Contains(string(body), `"description":""`) {
				t.Fatalf("epic create body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"description":""}}`), nil
		case "/api/projects/proj/epics/1":
			if !strings.Contains(string(body), `"description":""`) {
				t.Fatalf("epic edit body=%s", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"description":""}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{}}`), nil
	})
	cases := [][]string{
		{"issue", "create", "Title", "--body-file", "-"},
		{"issue", "edit", "1", "--body-file", "-"},
		{"issue", "comment", "create", "1", "--body-file", "-"},
		{"epic", "create", "Title", "--description-file", "-"},
		{"epic", "edit", "1", "--description-file", "-"},
	}
	for _, args := range cases {
		t.Run(args[0]+"-"+args[1], func(t *testing.T) {
			deps := Dependencies{
				HTTPClient: &http.Client{Transport: transport},
				Stdout:     &strings.Builder{},
				Stderr:     &strings.Builder{},
				Lookup: func(name string) (string, bool) {
					if name == "MOHIST_TOKEN" {
						return "token", true
					}
					if name == "MOHIST_SERVER_URL" {
						return "http://server", true
					}
					return "", false
				},
				ReadFile: func(path string) (string, error) {
					if v, ok := state[path]; ok {
						return v, nil
					}
					return "", os.ErrNotExist
				},
				HomeDir:           func() (string, error) { return "/home/test", nil },
				CurrentDirectory:  func() string { return "/work/tree" },
				Input:             strings.NewReader(""),
				OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
				ManagedPathExists: func(string) bool { return false },
			}
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d", code)
			}
		})
	}
}

// TestSessionFollowupAcceptsBlankWithAttachment proves the Session
// follow-up command allows blank text when --attach is supplied. The
// attachment-capable contract is owned by the Session command: the
// resolver returns the empty value, the command-owned blank check
// accepts it because attachments are present, and the carrier content is
// sent verbatim.
func TestSessionFollowupAcceptsBlankWithAttachment(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	cases := []struct {
		name string
		args []string
	}{
		{
			name: "explicit-empty-with-attach",
			args: []string{"session", "followup", "sess-1", "--text", "", "--attach", "log.txt"},
		},
		{
			name: "empty-file-with-attach",
			args: []string{"session", "followup", "sess-1", "--text-file", "./empty.md", "--attach", "log.txt"},
		},
		{
			name: "empty-stdin-with-attach",
			args: []string{"session", "followup", "sess-1", "--text-file", "-", "--attach", "log.txt"},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var sentBody string
			transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
				if r.URL.Path != "/api/projects/proj/agent-sessions/sess-1/followup" {
					t.Fatalf("path=%s", r.URL.Path)
				}
				b, _ := io.ReadAll(r.Body)
				sentBody = string(b)
				return response(http.StatusOK, `{"success":true,"data":{"sessionId":"sess-1","inputId":"input-1","turnId":"turn-1","status":"accepted"}}`), nil
			})
			deps := Dependencies{
				HTTPClient: &http.Client{Transport: transport},
				Stdout:     &strings.Builder{},
				Stderr:     &strings.Builder{},
				Lookup: func(name string) (string, bool) {
					if name == "MOHIST_TOKEN" {
						return "token", true
					}
					if name == "MOHIST_SERVER_URL" {
						return "http://server", true
					}
					return "", false
				},
				ReadFile: func(path string) (string, error) {
					if path == "./empty.md" {
						return "", nil
					}
					if v, ok := state[path]; ok {
						return v, nil
					}
					return "", os.ErrNotExist
				},
				HomeDir:           func() (string, error) { return "/home/test", nil },
				CurrentDirectory:  func() string { return "/work/tree" },
				Input:             strings.NewReader(""),
				OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
				ManagedPathExists: func(string) bool { return false },
			}
			if code := Run(context.Background(), tc.args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, deps.Stderr.(*strings.Builder).String())
			}
			if !strings.Contains(sentBody, `"text":""`) {
				t.Fatalf("body did not include empty text verbatim: %s", sentBody)
			}
		})
	}
}

// TestNonblankValuesAreSentUntrimmed guards against a regression where
// the blank check or any other intermediate step replaces the resolved
// value with a trimmed variant. Nonblank content with significant
// leading, trailing, and interior whitespace must be sent exactly as
// resolved for every command that accepts a text carrier.
func TestNonblankValuesAreSentUntrimmed(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}
	body := "  leading  \n\tinterior\n  trailing  \n"
	cases := []struct {
		name     string
		args     []string
		wantPath string
		flag     string
		key      string
	}{
		{
			name:     "issue-create",
			args:     []string{"issue", "create", "Title"},
			wantPath: "/api/projects/proj/issues",
			flag:     "--body-file",
			key:      "body",
		},
		{
			name:     "issue-edit",
			args:     []string{"issue", "edit", "1"},
			wantPath: "/api/projects/proj/issues/1",
			flag:     "--body-file",
			key:      "body",
		},
		{
			name:     "issue-comment",
			args:     []string{"issue", "comment", "create", "1"},
			wantPath: "/api/projects/proj/issues/1/comments",
			flag:     "--body-file",
			key:      "body",
		},
		{
			name:     "epic-create",
			args:     []string{"epic", "create", "Title"},
			wantPath: "/api/projects/proj/epics/",
			flag:     "--description-file",
			key:      "description",
		},
		{
			name:     "epic-edit",
			args:     []string{"epic", "edit", "1"},
			wantPath: "/api/projects/proj/epics/1",
			flag:     "--description-file",
			key:      "description",
		},
		{
			name:     "workflow-create",
			args:     []string{"workflow", "create"},
			wantPath: "/api/projects/proj/workflow-profiles",
			flag:     "--file",
			key:      "definitionSource",
		},
		{
			name:     "workflow-edit",
			args:     []string{"workflow", "edit", "p-1"},
			wantPath: "/api/projects/proj/workflow-profiles/p-1",
			flag:     "--file",
			key:      "definitionSource",
		},
		{
			name:     "project-verification-set",
			args:     []string{"project", "workflow", "verification", "set"},
			wantPath: "/api/projects/proj/verification-command",
			flag:     "--command-file",
			key:      "command",
		},
		{
			name:     "project-prompt-set",
			args:     []string{"project", "workflow", "prompt", "set", "intro"},
			wantPath: "/api/projects/proj/workflow-profile/prompts/intro",
			flag:     "--body-file",
			key:      "body",
		},
		{
			name:     "agent-start",
			args:     []string{"agent", "start"},
			wantPath: "/api/projects/proj/agent-tasks",
			flag:     "--prompt-file",
			key:      "prompt",
		},
		{
			name:     "agent-launch",
			args:     []string{"agent", "launch", "agent_1"},
			wantPath: "/api/projects/proj/agents/agent_1/sessions",
			flag:     "--prompt-file",
			key:      "prompt",
		},
		{
			name:     "session-followup",
			args:     []string{"session", "followup", "sess-1"},
			wantPath: "/api/projects/proj/agent-sessions/sess-1/followup",
			flag:     "--text-file",
			key:      "text",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var got string
			transport := roundTripFunc(func(r *http.Request) (*http.Response, error) {
				if r.URL.EscapedPath() != tc.wantPath {
					t.Fatalf("path=%s want=%s", r.URL.EscapedPath(), tc.wantPath)
				}
				b, _ := io.ReadAll(r.Body)
				got = string(b)
				return response(http.StatusOK, `{"success":true,"data":{}}`), nil
			})
			deps := Dependencies{
				HTTPClient: &http.Client{Transport: transport},
				Stdout:     &strings.Builder{},
				Stderr:     &strings.Builder{},
				Lookup: func(name string) (string, bool) {
					if name == "MOHIST_TOKEN" {
						return "token", true
					}
					if name == "MOHIST_SERVER_URL" {
						return "http://server", true
					}
					return "", false
				},
				ReadFile: func(path string) (string, error) {
					if path == "./source.txt" {
						return body, nil
					}
					if v, ok := state[path]; ok {
						return v, nil
					}
					return "", os.ErrNotExist
				},
				HomeDir:           func() (string, error) { return "/home/test", nil },
				CurrentDirectory:  func() string { return "/work/tree" },
				Input:             strings.NewReader(""),
				OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
				ManagedPathExists: func(string) bool { return false },
			}
			args := append([]string{}, tc.args...)
			args = append(args, tc.flag, "./source.txt")
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, deps.Stderr.(*strings.Builder).String())
			}
			wantSub := `"` + tc.key + `":"  leading  \n\tinterior\n  trailing  \n"`
			if !strings.Contains(got, wantSub) {
				t.Fatalf("body did not preserve whitespace verbatim: %s", got)
			}
		})
	}
}

// TestCarrierDiagnosticDistinctionForSameCommand proves that the two
// distinct failure modes share ExitUsage=2 but produce different
// diagnostics. A read failure of a blank-required carrier returns the
// "could not read" message while a successfully read blank value
// returns the "must not be blank" message so callers can distinguish
// them despite the shared exit code.
func TestCarrierDiagnosticDistinctionForSameCommand(t *testing.T) {
	state := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"proj"}`}

	// Read failure path: missing --prompt-file returns "could not read".
	failFixture := newCommandFixture(state)
	failFixture.deps.ReadFile = func(path string) (string, error) {
		failFixture.readPaths.add(path)
		if v, ok := state[path]; ok {
			return v, nil
		}
		return "", os.ErrNotExist
	}
	failCode := Run(context.Background(), []string{"agent", "start", "--prompt-file", "./missing.md"}, failFixture.deps)
	if failCode != ExitUsage {
		t.Fatalf("read-fail code=%d stderr=%q", failCode, failFixture.stderr.String())
	}
	if !strings.Contains(failFixture.stderr.String(), "could not read --prompt-file") {
		t.Fatalf("read-fail stderr missing 'could not read': %q", failFixture.stderr.String())
	}

	// Blank-success path: --prompt-file present but content is blank
	// (whitespace only) returns "must not be blank".
	blankFixture := newCommandFixture(state)
	blankFixture.deps.ReadFile = func(path string) (string, error) {
		blankFixture.readPaths.add(path)
		if v, ok := state[path]; ok {
			return v, nil
		}
		return "   \t\n   ", nil
	}
	blankCode := Run(context.Background(), []string{"agent", "start", "--prompt-file", "./blank.md"}, blankFixture.deps)
	if blankCode != ExitUsage {
		t.Fatalf("blank-success code=%d stderr=%q", blankCode, blankFixture.stderr.String())
	}
	if !strings.Contains(blankFixture.stderr.String(), "--prompt must not be blank") {
		t.Fatalf("blank-success stderr missing 'must not be blank': %q", blankFixture.stderr.String())
	}
	// The two diagnostics must be distinct even though both return
	// ExitUsage=2.
	if failFixture.stderr.String() == blankFixture.stderr.String() {
		t.Fatalf("diagnostics were identical for distinct failures")
	}
	if strings.Contains(blankFixture.stderr.String(), "could not read") {
		t.Fatalf("blank-success path leaked 'could not read': %q", blankFixture.stderr.String())
	}
	if strings.Contains(failFixture.stderr.String(), "must not be blank") {
		t.Fatalf("read-fail path leaked 'must not be blank': %q", failFixture.stderr.String())
	}
	// Neither path must issue any HTTP request or write to disk.
	if failFixture.transport.calls != 0 || blankFixture.transport.calls != 0 {
		t.Fatalf("HTTP calls fail=%d blank=%d", failFixture.transport.calls, blankFixture.transport.calls)
	}
	if len(failFixture.writtenPaths.paths) != 0 || len(blankFixture.writtenPaths.paths) != 0 {
		t.Fatalf("writes fail=%v blank=%v", failFixture.writtenPaths.paths, blankFixture.writtenPaths.paths)
	}
}
