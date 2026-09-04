package mohistcli

import (
	"bytes"
	"context"
	"errors"
	"net/http"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

type recordingManagedUpdateRuntime struct {
	requests []ManagedUpdateRequest
	err      error
}

func (runtime *recordingManagedUpdateRuntime) Update(_ context.Context, request ManagedUpdateRequest) error {
	request.Components = append([]string(nil), request.Components...)
	runtime.requests = append(runtime.requests, request)
	return runtime.err
}

type recordingMaintenanceFiles struct {
	values   map[string]string
	accesses []string
}

func (files *recordingMaintenanceFiles) ReadFile(path string) (string, error) {
	files.accesses = append(files.accesses, "read "+path)
	value, ok := files.values[path]
	if !ok {
		return "", os.ErrNotExist
	}
	return value, nil
}

func (files *recordingMaintenanceFiles) WriteFile(path, value string, _ os.FileMode) error {
	files.accesses = append(files.accesses, "write "+path)
	files.values[path] = value
	return nil
}

func TestInstallRunnerPersistsEnabledAgentRuntimes(t *testing.T) {
	home := t.TempDir()
	repoRoot := t.TempDir()
	runnerRoot := filepath.Join(home, "custom runner")
	files := map[string]string{}
	modes := map[string]os.FileMode{}
	var commands [][]string
	var enrollmentRequests int
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		enrollmentRequests++
		if request.Method != http.MethodPost || request.URL.Path != "/api/runners/enrollment-tokens" || request.URL.Host != "managed-server" {
			t.Fatalf("unexpected enrollment request: %s %s", request.Method, request.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":{"token":"enrollment-token"}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.CurrentDirectory = func() string { return repoRoot }
	deps.WriteFile = func(path, value string, mode os.FileMode) error {
		files[path] = value
		modes[path] = mode
		return nil
	}
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	code := Run(context.Background(), []string{
		"install", "runner", "--repo-root", repoRoot,
		"--server-url", "https://managed-server", "--runner-root", runnerRoot,
		"--enabled-agent-runtimes", " OpenCode,pi,opencode ",
	}, deps)
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if enrollmentRequests != 1 {
		t.Fatalf("enrollment requests = %d", enrollmentRequests)
	}
	environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
	if files[environmentPath] != "ENABLED_AGENT_RUNTIMES=pi,opencode\n" {
		t.Fatalf("environment file = %q", files[environmentPath])
	}
	if modes[environmentPath] != 0o600 {
		t.Fatalf("environment file mode = %o", modes[environmentPath])
	}
	enrollmentTokenPath := filepath.Join(runnerRoot, runnerEnrollmentTokenFile)
	if files[enrollmentTokenPath] != "enrollment-token\n" {
		t.Fatalf("enrollment bootstrap = %q", files[enrollmentTokenPath])
	}
	if modes[enrollmentTokenPath] != 0o600 {
		t.Fatalf("enrollment bootstrap mode = %o", modes[enrollmentTokenPath])
	}
	managedEnvironmentPath := filepath.Join(home, ".config", "mohist", "runner-managed.env")
	wantManagedEnvironment := "SERVER_URL=\"https://managed-server\"\nRUNNER_ROOT=\"" + runnerRoot + "\"\n"
	if files[managedEnvironmentPath] != wantManagedEnvironment {
		t.Fatalf("managed environment = %q, want %q", files[managedEnvironmentPath], wantManagedEnvironment)
	}
	if modes[managedEnvironmentPath] != 0o600 {
		t.Fatalf("managed environment mode = %o", modes[managedEnvironmentPath])
	}
	unitPath := filepath.Join(home, ".config", "systemd", "user", "mohist-runner.service")
	unit := files[unitPath]
	if !strings.Contains(unit, "EnvironmentFile=-%h/.config/mohist/runner.env\n") {
		t.Fatalf("unit does not reference managed environment file: %q", unit)
	}
	if !strings.Contains(unit, "EnvironmentFile=-%h/.config/mohist/runner-managed.env\n") {
		t.Fatalf("unit does not reference the managed connection environment: %q", unit)
	}
	if !strings.Contains(unit, "ExecStart=node packages/runner/dist/cli.js\n") {
		t.Fatalf("unit does not use the built Runner CLI entrypoint: %q", unit)
	}
	if strings.Contains(unit, "operator-token") || strings.Contains(unit, "enrollment-token") {
		t.Fatalf("unit leaked a credential: %q", unit)
	}
	if strings.Contains(out.String(), "enrollment-token") || strings.Contains(errOut.String(), "enrollment-token") {
		t.Fatalf("command output leaked the enrollment token: stdout=%q stderr=%q", out, errOut)
	}
	wantCommands := [][]string{
		{"systemctl", "--user", "daemon-reload"},
		{"systemctl", "--user", "enable", "mohist-runner.service"},
		{"systemctl", "--user", "restart", "mohist-runner.service"},
	}
	if len(commands) != len(wantCommands) {
		t.Fatalf("commands = %#v", commands)
	}
	for index := range wantCommands {
		if strings.Join(commands[index], "\x00") != strings.Join(wantCommands[index], "\x00") {
			t.Fatalf("command %d = %#v, want %#v", index, commands[index], wantCommands[index])
		}
		if strings.Contains(strings.Join(commands[index], " "), "enrollment-token") {
			t.Fatalf("command %d leaked the enrollment token: %#v", index, commands[index])
		}
	}
}

func TestInstallRunnerEnabledAgentRuntimesValidation(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want string
	}{
		{name: "empty", args: []string{"install", "runner", "--enabled-agent-runtimes", ""}, want: "must be a non-empty"},
		{name: "empty member", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi,"}, want: "must be a non-empty"},
		{name: "unknown", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi,codex"}, want: "unknown Runtime"},
		{name: "line injection", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi\nINJECTED=value"}, want: "unknown Runtime"},
		{name: "missing value", args: []string{"install", "runner", "--enabled-agent-runtimes"}, want: "requires a value"},
		{name: "server scope", args: []string{"install", "server", "--enabled-agent-runtimes", "pi"}, want: "only valid with mo install runner"},
		{name: "update scope", args: []string{"update", "runner", "--enabled-agent-runtimes", "pi"}, want: "only valid with mo install runner"},
		{name: "duplicate flag", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi", "--enabled-agent-runtimes", "opencode"}, want: "only once"},
		{name: "bare update cli path", args: []string{"update", "--cli-path", "/tmp/mo"}, want: "only valid with mo update cli"},
		{name: "retired continuation", args: []string{"update", "--continue-after-cli-update"}, want: "unknown option"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			calls := 0
			deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("must not call")
			}), map[string]string{})
			if code := Run(context.Background(), test.args, deps); code != ExitUsage {
				t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
			}
			if calls != 0 {
				t.Fatalf("HTTP calls = %d", calls)
			}
			if !strings.Contains(errOut.String(), test.want) {
				t.Fatalf("stderr = %q, want %q", errOut.String(), test.want)
			}
		})
	}
}

func TestInstallComponentRunnerRejectsEmptyEnrollmentTokenBeforeSideEffects(t *testing.T) {
	writes := 0
	executes := 0
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.CurrentDirectory = func() string { return "/repo" }
	deps.WriteFile = func(string, string, os.FileMode) error {
		writes++
		return nil
	}
	deps.Execute = func(context.Context, string, []string) error {
		executes++
		return nil
	}

	code := installComponent(context.Background(), deps, "runner", command{}, "", "")

	if code != ExitOperation {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if writes != 0 || executes != 0 {
		t.Fatalf("side effects before enrollment: writes=%d executes=%d", writes, executes)
	}
	if !strings.Contains(errOut.String(), "runner enrollment token is required") {
		t.Fatalf("stderr = %q", errOut.String())
	}
}

func TestInstallRunnerWithoutRuntimeFlagDoesNotCreateEnvironmentFile(t *testing.T) {
	home := t.TempDir()
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"token":"enrollment-token"}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.CurrentDirectory = func() string { return t.TempDir() }
	deps.Execute = func(context.Context, string, []string) error { return nil }

	if code := Run(context.Background(), []string{"install", "runner"}, deps); code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
	if _, err := os.Stat(environmentPath); !os.IsNotExist(err) {
		t.Fatalf("environment file was created without the flag: %v", err)
	}
	unitPath := filepath.Join(home, ".config", "systemd", "user", "mohist-runner.service")
	unit, err := os.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(unit), "EnvironmentFile=-%h/.config/mohist/runner.env\n") {
		t.Fatalf("unit does not tolerate the absent environment file: %q", unit)
	}
}

func TestInstallRunnerBootstrapWriteFailureHasNoSystemdEffects(t *testing.T) {
	home := t.TempDir()
	var commands [][]string
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"token":"enrollment-token"}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.CurrentDirectory = func() string { return "/repo" }
	deps.WriteFile = func(path, _ string, _ os.FileMode) error {
		if path == filepath.Join(home, ".mohist", "projects", runnerEnrollmentTokenFile) {
			return errors.New("bootstrap unavailable")
		}
		t.Fatalf("unexpected write after bootstrap failure: %s", path)
		return nil
	}
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	if code := Run(context.Background(), []string{"install", "runner"}, deps); code != ExitOperation {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if len(commands) != 0 {
		t.Fatalf("systemd commands after bootstrap failure: %#v", commands)
	}
}

func TestRunnerManagedEnvironmentEscapesValuesAndRejectsLineInjection(t *testing.T) {
	got, err := runnerManagedEnvironment(`https://server/"quoted"`, `C:\runner path`)
	if err != nil {
		t.Fatal(err)
	}
	want := "SERVER_URL=\"https://server/\\\"quoted\\\"\"\nRUNNER_ROOT=\"C:\\\\runner path\"\n"
	if got != want {
		t.Fatalf("managed environment = %q, want %q", got, want)
	}
	for _, test := range []struct {
		name       string
		serverURL  string
		runnerRoot string
	}{
		{name: "server newline", serverURL: "https://server\nINJECTED=value", runnerRoot: "/runner"},
		{name: "root newline", serverURL: "https://server", runnerRoot: "/runner\nINJECTED=value"},
		{name: "root nul", serverURL: "https://server", runnerRoot: "/runner\x00tail"},
	} {
		t.Run(test.name, func(t *testing.T) {
			if _, err := runnerManagedEnvironment(test.serverURL, test.runnerRoot); err == nil {
				t.Fatal("expected invalid managed environment value")
			}
		})
	}
}

func TestUpdateRunnerDelegatesScopeAndPreservesRuntimeEnvironment(t *testing.T) {
	home := "/home/operator"
	repoRoot := "/source checkout"
	unitDir := "/unit directory"
	environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
	files := &recordingMaintenanceFiles{values: map[string]string{
		environmentPath: "ENABLED_AGENT_RUNTIMES=opencode\n",
	}}
	wantEnvironment := files.values[environmentPath]
	runtime := &recordingManagedUpdateRuntime{}
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.ManagedUpdate = runtime
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = files.ReadFile
	deps.WriteFile = files.WriteFile
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	code := Run(context.Background(), []string{
		"update", "runner", "--repo-root", repoRoot, "--unit-dir", unitDir,
	}, deps)
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if len(runtime.requests) != 1 {
		t.Fatalf("managed update requests = %#v", runtime.requests)
	}
	request := runtime.requests[0]
	if len(request.Components) != 1 || request.Components[0] != "runner" ||
		request.RepoRoot != repoRoot || request.UnitDir != unitDir || request.DryRun {
		t.Fatalf("managed update request = %#v", request)
	}
	if len(files.accesses) != 0 {
		t.Fatalf("managed update routing accessed filesystem: %#v", files.accesses)
	}
	if len(commands) != 0 {
		t.Fatalf("managed update routing executed commands: %#v", commands)
	}
	if files.values[environmentPath] != wantEnvironment {
		t.Fatalf("environment file changed to %q", files.values[environmentPath])
	}
}

func TestUpdateDefaultsToOneManagedServerRunnerTransaction(t *testing.T) {
	runtime := &recordingManagedUpdateRuntime{}
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.ManagedUpdate = runtime
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	code := Run(context.Background(), []string{"update", "--repo-root", "/exact/source"}, deps)
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if len(runtime.requests) != 1 {
		t.Fatalf("managed update requests = %#v", runtime.requests)
	}
	request := runtime.requests[0]
	if !reflect.DeepEqual(request.Components, []string{"server", "runner"}) || request.RepoRoot != "/exact/source" {
		t.Fatalf("managed update request = %#v", request)
	}
	if len(commands) != 0 {
		t.Fatalf("default update escaped the managed transaction: %#v", commands)
	}
}

func TestUpdateRunnerPreservesCredentialFiles(t *testing.T) {
	home := "/home/operator"
	repoRoot := "/source checkout"
	unitDir := "/unit directory"
	runnerRoot := filepath.Join(home, ".mohist", "projects")
	enrollmentTokenPath := filepath.Join(runnerRoot, runnerEnrollmentTokenFile)
	credentialPath := filepath.Join(runnerRoot, "credential")
	environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
	managedEnvironmentPath := filepath.Join(home, ".config", "mohist", "runner-managed.env")
	wantFiles := map[string]string{
		enrollmentTokenPath:    "pending-enrollment\n",
		credentialPath:         "machine-credential\n",
		environmentPath:        "ENABLED_AGENT_RUNTIMES=pi\n",
		managedEnvironmentPath: "SERVER_URL=\"https://managed\"\nRUNNER_ROOT=\"/custom\"\n",
	}
	files := &recordingMaintenanceFiles{values: map[string]string{}}
	for path, value := range wantFiles {
		files.values[path] = value
	}
	runtime := &recordingManagedUpdateRuntime{}
	deps, out, errOut := testDeps(nil, map[string]string{})
	deps.ManagedUpdate = runtime
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = files.ReadFile
	deps.WriteFile = files.WriteFile
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	if code := Run(context.Background(), []string{
		"update", "runner", "--repo-root", repoRoot, "--unit-dir", unitDir, "--dry-run",
	}, deps); code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if len(runtime.requests) != 1 {
		t.Fatalf("managed update requests = %#v", runtime.requests)
	}
	request := runtime.requests[0]
	if len(request.Components) != 1 || request.Components[0] != "runner" ||
		request.RepoRoot != repoRoot || request.UnitDir != unitDir || !request.DryRun {
		t.Fatalf("managed update request = %#v", request)
	}
	if len(files.accesses) != 0 {
		t.Fatalf("managed update routing accessed filesystem: %#v", files.accesses)
	}
	if len(commands) != 0 {
		t.Fatalf("managed update routing executed commands: %#v", commands)
	}
	for path, want := range wantFiles {
		if files.values[path] != want {
			t.Fatalf("%s changed to %q", path, files.values[path])
		}
	}
	combined := out.String() + errOut.String()
	if strings.Contains(combined, "pending-enrollment") || strings.Contains(combined, "machine-credential") {
		t.Fatalf("update leaked credentials: %q", combined)
	}
}

func TestInstallHelpDocumentsRunnerRuntimeSelection(t *testing.T) {
	deps, out, errOut := testDeps(nil, map[string]string{})
	if code := Run(context.Background(), []string{"install", "runner", "--help"}, deps); code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if !strings.Contains(out.String(), "mo install runner [--enabled-agent-runtimes <list>]") {
		t.Fatalf("help = %q", out.String())
	}
}

func writeTestSkill(t *testing.T, root, name string) {
	t.Helper()
	path := filepath.Join(root, name, "SKILL.md")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	writeTestSkillContent(t, path, "body")
}

func writeTestSkillContent(t *testing.T, path, body string) {
	t.Helper()
	if err := os.WriteFile(path, []byte("---\nname: "+filepath.Base(filepath.Dir(path))+"\ndescription: test skill\n---\n\n"+body+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
}

func TestSkillListUsesLocalAssetsAndFieldSelection(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, root, "mohist")
	writeTestSkill(t, root, "mohist-explore")
	var stdout, stderr bytes.Buffer
	code := Run(context.Background(), []string{"skill", "list", "--json", "name"}, Dependencies{
		Stdout: &stdout,
		Stderr: &stderr,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_SKILLS_DIR" {
				return root, true
			}
			return "", false
		},
		HomeDir:          func() (string, error) { return t.TempDir(), nil },
		Executable:       func() string { return filepath.Join(t.TempDir(), "mo") },
		CurrentDirectory: func() string { return t.TempDir() },
	})
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, stderr.String())
	}
	if got := stdout.String(); !strings.Contains(got, `"name":"mohist"`) || strings.Contains(got, "description") {
		t.Fatalf("unexpected JSON: %s", got)
	}
}

func TestSkillInstallUsesPrivateFileMode(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, root, "mohist")
	var stdout, stderr bytes.Buffer
	var writtenPath string
	var writtenMode os.FileMode
	code := Run(context.Background(), []string{"skill", "install", "--path", root}, Dependencies{
		Stdout: &stdout,
		Stderr: &stderr,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_SKILLS_DIR" {
				return root, true
			}
			return "", false
		},
		HomeDir:          func() (string, error) { return root, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return filepath.Join(root, "mo") },
		WriteFile:        func(path, value string, mode os.FileMode) error { writtenPath, writtenMode = path, mode; return nil },
	})
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, stderr.String())
	}
	if !strings.HasSuffix(writtenPath, filepath.Join(".agents", "skills", "mohist", "SKILL.md")) || writtenMode != 0o600 {
		t.Fatalf("path/mode = %s/%o", writtenPath, writtenMode)
	}
}

func TestUpdateCLIFailureKeepsExistingExecutable(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(root, "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	var stdout, stderr bytes.Buffer
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		Stdout:           &stdout,
		Stderr:           &stderr,
		Lookup:           func(string) (string, bool) { return "", false },
		HomeDir:          func() (string, error) { return root, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute:          func(context.Context, string, []string) error { return errors.New("build failed") },
	})
	if code != ExitOperation {
		t.Fatalf("exit code = %d", code)
	}
	data, err := os.ReadFile(target)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "old" {
		t.Fatalf("target changed to %q", data)
	}
	if _, err := os.Stat(target + ".tmp"); !os.IsNotExist(err) {
		t.Fatalf("temporary binary remains: %v", err)
	}
}

func TestUpdateCLIUsesCanonicalGoAssets(t *testing.T) {
	root := t.TempDir()
	goCLI := canonicalGoCLIPath(root)
	writeTestSkill(t, canonicalGoSkillDataPath(root), "target-skill")
	writeTestSkill(t, filepath.Join(root, "packages", "cli", "skill-data"), "wrong-skill")
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	writeTestSkill(t, filepath.Join(home, ".mohist", "cli", "skill-data"), "old-skill")
	var stdout, stderr bytes.Buffer
	var buildName string
	var buildArgs []string
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		Stdout:           &stdout,
		Stderr:           &stderr,
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return filepath.Join(t.TempDir(), "caller") },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, name string, args []string) error {
			buildName, buildArgs = name, append([]string(nil), args...)
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
	})
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, stderr.String())
	}
	if buildName != "go" || len(buildArgs) < 2 || buildArgs[0] != "-C" || buildArgs[1] != goCLI {
		t.Fatalf("build command = %s %v", buildName, buildArgs)
	}
	data, err := os.ReadFile(target)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "new" {
		t.Fatalf("binary = %q", data)
	}
	if _, err := os.Stat(filepath.Join(home, ".mohist", "cli", "skill-data", "target-skill", "SKILL.md")); err != nil {
		t.Fatalf("target skill was not synchronized: %v", err)
	}
	if _, err := os.Stat(filepath.Join(home, ".mohist", "cli", "skill-data", "wrong-skill")); !os.IsNotExist(err) {
		t.Fatalf("non-canonical skill source was used: %v", err)
	}
}

func TestUpdateCLIMissingSkillTreePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
	})
	if code != ExitOperation {
		t.Fatalf("exit code = %d", code)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func TestUpdateCLIRenameFailurePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, canonicalGoSkillDataPath(root), "new-skill")
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	var failTarget string
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
		Rename: func(old, new string) error {
			if failTarget == "" && new == managed {
				failTarget = new
				return errors.New("managed tree rename failed")
			}
			return os.Rename(old, new)
		},
	})
	if code != ExitOperation || failTarget != managed {
		t.Fatalf("code = %d, failed target = %q", code, failTarget)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func TestUpdateCLIFinalBinaryRenameFailurePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, canonicalGoSkillDataPath(root), "new-skill")
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	failed := false
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
		Rename: func(old, new string) error {
			if new == target && !failed {
				failed = true
				return errors.New("binary rename failed")
			}
			return os.Rename(old, new)
		},
	})
	if code != ExitOperation {
		t.Fatalf("code = %d", code)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func TestUpdateCLIInvalidSkillTreePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	skillData := canonicalGoSkillDataPath(root)
	path := filepath.Join(skillData, "broken", "SKILL.md")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("not valid skill frontmatter"), 0o600); err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
	})
	if code != ExitOperation {
		t.Fatalf("exit code = %d", code)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func assertInstallationPreserved(t *testing.T, binary, skills, skillName string) {
	t.Helper()
	data, err := os.ReadFile(binary)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "old" {
		t.Fatalf("binary changed to %q", data)
	}
	if _, err := os.Stat(filepath.Join(skills, skillName, "SKILL.md")); err != nil {
		t.Fatalf("managed skills changed: %v", err)
	}
}
