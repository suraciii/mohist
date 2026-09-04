package mohistcli

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"sort"
	"strings"
	"testing"
	"time"
)

const (
	managedBuildTestRuntimeRoot = "/managed-runtime"
	managedBuildTestTransaction = "/managed-runtime/transactions/build-test"
	managedBuildTestBuildRoot   = "/managed-runtime/transactions/build-test/build/source"
	managedBuildTestCommit      = "0123456789abcdef0123456789abcdef01234567"
	managedBuildTestTree        = "89abcdef0123456789abcdef0123456789abcdef"
)

func TestStageManagedTargetsServerStagesPublishCommand(t *testing.T) {
	files := newManagedBuildTestFiles()
	commands := newManagedBuildTestCommands(files)
	source := managedBuildTestSource()

	targets, err := stageManagedTargets(
		context.Background(), managedUpdateEnvironment{files: files, commands: commands},
		source, managedBuildTestTransaction, managedBuildTestRuntimeRoot, 19, []string{"server"}, "",
	)
	if err != nil {
		t.Fatalf("stageManagedTargets() error = %v", err)
	}

	want := []managedCommand{
		{Name: "npm", Args: []string{"ci", "--include=dev"}, Dir: managedBuildTestBuildRoot},
		{
			Name: "dotnet",
			Args: []string{
				"publish", filepath.Join(managedBuildTestBuildRoot, "packages", "server", "src", "Mohist.Server", "Mohist.Server.csproj"),
				"-c", "Release", "-r", managedRuntimeIdentifier(), "--self-contained", "true", "/p:PublishSingleFile=true",
				"/p:InformationalVersion=0.0.0+" + managedBuildTestCommit, "/p:SourceRevisionId=" + managedBuildTestCommit,
				"-o", filepath.Join(managedBuildTestTransaction, "candidate", "server"),
			},
			Dir: managedBuildTestBuildRoot,
		},
	}
	if !reflect.DeepEqual(commands.calls, want) {
		t.Fatalf("commands = %#v, want %#v", commands.calls, want)
	}
	target := targets["server"]
	if target == nil || target.LaunchMode != 0 || target.NodeExecutable != nil {
		t.Fatalf("server target = %#v, want native launch without node", target)
	}
	if !filepath.IsAbs(target.Entrypoint) || !filepath.IsAbs(target.WorkingDirectory) {
		t.Fatalf("server target paths are not absolute: %#v", target)
	}
}

func TestStageManagedTargetsRunnerStagesHoistedAndLocalDependencies(t *testing.T) {
	files := newManagedBuildTestFiles()
	files.put(filepath.Join(managedBuildTestBuildRoot, "packages", "runner", "dist", "cli.js"), []byte("runner entrypoint"))
	files.put(filepath.Join(managedBuildTestBuildRoot, "packages", "runner", "package.json"), []byte(`{"name":"runner"}`))
	files.put(filepath.Join(managedBuildTestBuildRoot, "node_modules", "hoisted.js"), []byte("hoisted"))
	files.put(filepath.Join(managedBuildTestBuildRoot, "packages", "runner", "node_modules", "local.js"), []byte("local"))
	commands := newManagedBuildTestCommands(files)
	source := managedBuildTestSource()

	targets, err := stageManagedTargets(
		context.Background(), managedUpdateEnvironment{files: files, commands: commands},
		source, managedBuildTestTransaction, managedBuildTestRuntimeRoot, 23, []string{"runner"}, "runner-pluto",
	)
	if err != nil {
		t.Fatalf("stageManagedTargets() error = %v", err)
	}

	wantNames := []string{"npm", "npm", "cp", "cp", "cp", "cp", "sh"}
	if len(commands.calls) != len(wantNames) {
		t.Fatalf("command count = %d, want %d: %#v", len(commands.calls), len(wantNames), commands.calls)
	}
	for index, wantName := range wantNames {
		if commands.calls[index].Name != wantName {
			t.Fatalf("command %d name = %q, want %q", index, commands.calls[index].Name, wantName)
		}
	}
	for index := 2; index < 6; index++ {
		if !reflect.DeepEqual(commands.calls[index].Args[:1], []string{"-RLp"}) {
			t.Fatalf("copy command %d args = %#v, want -RLp", index, commands.calls[index].Args)
		}
		if commands.calls[index].Dir != managedBuildTestBuildRoot {
			t.Fatalf("copy command %d dir = %q, want %q", index, commands.calls[index].Dir, managedBuildTestBuildRoot)
		}
	}
	if !reflect.DeepEqual(commands.calls[1].Args, []string{"run", "build", "-w", "packages/runner"}) {
		t.Fatalf("runner build args = %#v", commands.calls[1].Args)
	}

	candidate := filepath.Join(managedBuildTestTransaction, "candidate", "runner")
	if got, _, err := files.ReadFile(filepath.Join(candidate, "node_modules", "hoisted.js")); err != nil || string(got) != "hoisted" {
		t.Fatalf("hoisted dependency = %q, %v", got, err)
	}
	if got, _, err := files.ReadFile(filepath.Join(candidate, "node_modules", "local.js")); err != nil || string(got) != "local" {
		t.Fatalf("local dependency = %q, %v", got, err)
	}
	if got, _, err := files.ReadFile(filepath.Join(candidate, "dist", "cli.js")); err != nil || string(got) != "runner entrypoint" {
		t.Fatalf("runner entrypoint = %q, %v", got, err)
	}
	if got, _, err := files.ReadFile(filepath.Join(candidate, "package.json")); err != nil || string(got) != `{"name":"runner"}` {
		t.Fatalf("runner package manifest = %q, %v", got, err)
	}
	target := targets["runner"]
	if target == nil || target.NodeExecutable == nil || *target.NodeExecutable != "/usr/bin/node" {
		t.Fatalf("runner node executable = %#v, want absolute /usr/bin/node", target)
	}
	if target.DependencyRoot == nil || *target.DependencyRoot != target.WorkingDirectory {
		t.Fatalf("runner dependency root = %#v, want release working directory", target.DependencyRoot)
	}
	if target.LaunchMode != 1 || !target.UsesCanonicalEntrypoint {
		t.Fatalf("runner launch target = %#v", target)
	}
}

func TestStageManagedTargetsRejectsMissingRunnerEntrypoint(t *testing.T) {
	files := newManagedBuildTestFiles()
	files.put(filepath.Join(managedBuildTestBuildRoot, "packages", "runner", "package.json"), []byte(`{"name":"runner"}`))
	commands := newManagedBuildTestCommands(files)

	_, err := stageManagedTargets(
		context.Background(), managedUpdateEnvironment{files: files, commands: commands},
		managedBuildTestSource(), managedBuildTestTransaction, managedBuildTestRuntimeRoot, 29, []string{"runner"}, "runner-pluto",
	)
	if err == nil || !strings.Contains(err.Error(), "runner publish did not produce its required entrypoint") {
		t.Fatalf("error = %v, want missing runner entrypoint", err)
	}
	if files.has(filepath.Join(managedBuildTestTransaction, "candidate", "runner", "runtime-identity.json")) {
		t.Fatal("runtime identity was written after missing entrypoint")
	}
}

func TestManagedUpdaterBuildFailurePrecedesServiceActivation(t *testing.T) {
	files := newManagedBuildTestFiles()
	commands := newManagedBuildTestCommands(files)
	commands.failPublish = true
	control := &managedBuildTestControl{}
	previous := managedBuildTestServerTarget()
	runtimeRoot := filepath.Join("/home/test", ".local", "share", "mohist", "runtime")
	activePath := filepath.Join(runtimeRoot, "active.json")
	verifiedPath := filepath.Join(runtimeRoot, "verified.json")
	files.putJSON(activePath, managedPointer{
		"status":     json.RawMessage(`"verified"`),
		"generation": json.RawMessage(`7`),
		"server":     managedBuildTestJSON(previous),
	})
	files.putJSON(verifiedPath, managedPointer{
		"status":     json.RawMessage(`"verified"`),
		"generation": json.RawMessage(`7`),
		"server":     managedBuildTestJSON(previous),
	})
	unitPath := filepath.Join("/home/test", ".config", "systemd", "user", "mohist.service")
	commands.unitPath = unitPath
	files.put(unitPath, []byte("[Unit]\nDescription=managed\n[Service]\nWorkingDirectory=\"/old\"\nExecStart=\"/old/Mohist.Server\"\nEnvironment=MOHIST_RUNTIME_IDENTITY_PATH=\"/old/runtime-identity.json\"\n[Install]\nWantedBy=default.target\n"))
	files.put("/repo/Mohist.sln", []byte("solution"))

	env := managedUpdateEnvironment{
		files: files, commands: commands, control: control,
		now:     func() time.Time { return time.Unix(1700000000, 0).UTC() },
		wait:    func(context.Context, time.Duration) error { return errors.New("unexpected wait") },
		newID:   func() string { return "tx-build-failure" },
		homeDir: func() (string, error) { return "/home/test", nil },
		stdout:  io.Discard, stderr: io.Discard,
	}
	err := (&managedUpdater{env: env}).Update(context.Background(), ManagedUpdateRequest{Components: []string{"server"}, RepoRoot: "/repo"})
	if err == nil || !strings.Contains(err.Error(), "server publish failed") {
		t.Fatalf("Update() error = %v, want publish failure", err)
	}
	if control.beginCalls != 0 {
		t.Fatalf("runner interrupt calls = %d, want 0", control.beginCalls)
	}
	for _, command := range commands.calls {
		if command.Name == "systemctl" && (containsArg(command.Args, "restart") || containsArg(command.Args, "daemon-reload")) {
			t.Fatalf("service activation command ran after build failure: %#v", command)
		}
	}
	unit, _, readErr := files.ReadFile(unitPath)
	if readErr != nil || !strings.Contains(string(unit), "WorkingDirectory=\"/old\"") {
		t.Fatalf("service unit changed before activation: %q, %v", unit, readErr)
	}
}

func TestManagedArtifactDigestSortsPathsAndExcludesMetadata(t *testing.T) {
	root := "/candidate"
	files := newManagedBuildTestFiles()
	files.put(filepath.Join(root, "z.txt"), []byte("z-value"))
	files.put(filepath.Join(root, "a.txt"), []byte("a-value"))
	files.put(filepath.Join(root, "runtime-identity.json"), []byte("metadata one"))
	files.put(filepath.Join(root, "release.json"), []byte("metadata two"))
	files.put(filepath.Join(root, "dist", "build-info.json"), []byte("metadata three"))
	files.walkOrder[root] = []string{"z.txt", "dist/build-info.json", "a.txt", "release.json", "runtime-identity.json"}

	got, err := managedArtifactDigest(files, root)
	if err != nil {
		t.Fatalf("managedArtifactDigest() error = %v", err)
	}
	wantHash := sha256.New()
	_, _ = wantHash.Write([]byte("a.txt\n7\na-valuez.txt\n7\nz-value"))
	want := hex.EncodeToString(wantHash.Sum(nil))
	if got != want {
		t.Fatalf("digest = %s, want %s", got, want)
	}

	files.put(filepath.Join(root, "runtime-identity.json"), []byte("changed identity"))
	files.put(filepath.Join(root, "release.json"), []byte("changed release"))
	files.put(filepath.Join(root, "dist", "build-info.json"), []byte("changed build info"))
	changedMetadataDigest, err := managedArtifactDigest(files, root)
	if err != nil || changedMetadataDigest != got {
		t.Fatalf("metadata changed digest = %s, %v; want unchanged %s", changedMetadataDigest, err, got)
	}

	files.put(filepath.Join(root, "node_modules", "dependency", "build-info.json"), []byte("dependency payload"))
	files.walkOrder[root] = append(files.walkOrder[root], "node_modules/dependency/build-info.json")
	changedPayloadDigest, err := managedArtifactDigest(files, root)
	if err != nil || changedPayloadDigest == got {
		t.Fatalf("nested payload digest = %s, %v; want different from %s", changedPayloadDigest, err, got)
	}
}

func TestWriteManagedMetadataUsesExactCamelCaseIdentity(t *testing.T) {
	files := newManagedBuildTestFiles()
	identity := managedRuntimeIdentity{
		Component: "runner", Version: "0.0.0+" + managedBuildTestCommit,
		SourceRevision: managedBuildTestCommit, TreeHash: managedBuildTestTree,
		ArtifactDigest: strings.Repeat("d", 64), ReleaseID: "mohist-runner-" + managedBuildTestCommit,
		Generation: 31, RunnerID: "runner-pluto", BuildGitHash: managedBuildTestCommit, IsComplete: true,
	}
	err := writeManagedMetadata(files, "/candidate/runner", managedBuildTestSource(), identity)
	if err != nil {
		t.Fatalf("writeManagedMetadata() error = %v", err)
	}

	assertExactJSONKeys(t, files, "/candidate/runner/runtime-identity.json", []string{
		"artifactDigest", "buildGitHash", "component", "generation", "isComplete", "releaseId", "runnerId", "sourceRevision", "treeHash", "version",
	})
	assertExactJSONKeys(t, files, "/candidate/runner/release.json", []string{"identity", "snapshotRoot", "sourceRoot"})
	assertExactNestedJSONKeys(t, files, "/candidate/runner/release.json", "identity", []string{
		"artifactDigest", "buildGitHash", "component", "generation", "isComplete", "releaseId", "runnerId", "sourceRevision", "treeHash", "version",
	})
	assertExactJSONKeys(t, files, "/candidate/runner/dist/build-info.json", []string{
		"artifactDigest", "component", "generation", "gitHash", "releaseId", "runnerId", "sourceRevision", "treeHash", "version",
	})
}

func TestStageManagedTargetsUsesGenerationInAbsoluteReleaseLayout(t *testing.T) {
	files := newManagedBuildTestFiles()
	commands := newManagedBuildTestCommands(files)
	targets, err := stageManagedTargets(
		context.Background(), managedUpdateEnvironment{files: files, commands: commands},
		managedBuildTestSource(), managedBuildTestTransaction, managedBuildTestRuntimeRoot, 41, []string{"server"}, "",
	)
	if err != nil {
		t.Fatalf("stageManagedTargets() error = %v", err)
	}

	target := targets["server"]
	wantReleaseRoot := filepath.Join(managedBuildTestRuntimeRoot, "releases", "mohist-server-"+managedBuildTestCommit+"-g41", "server")
	if target.WorkingDirectory != wantReleaseRoot {
		t.Fatalf("working directory = %q, want %q", target.WorkingDirectory, wantReleaseRoot)
	}
	if target.Entrypoint != filepath.Join(wantReleaseRoot, "Mohist.Server") {
		t.Fatalf("entrypoint = %q, want %q", target.Entrypoint, filepath.Join(wantReleaseRoot, "Mohist.Server"))
	}
	if target.Identity.Generation != 41 || target.Identity.ReleaseID != "mohist-server-"+managedBuildTestCommit {
		t.Fatalf("identity = %#v, want generation 41 and server release identity", target.Identity)
	}
	if !target.IsAbsoluteTarget || !filepath.IsAbs(target.Entrypoint) || !filepath.IsAbs(target.WorkingDirectory) {
		t.Fatalf("target paths/trust = %#v", target)
	}
}

func managedBuildTestSource() managedSource {
	return managedSource{
		RepositoryRoot: "/repo", Commit: managedBuildTestCommit, TreeHash: managedBuildTestTree,
		SnapshotRoot: "/managed-runtime/transactions/build-test/snapshot",
		BuildRoot:    managedBuildTestBuildRoot,
	}
}

func managedBuildTestServerTarget() *managedRuntimeTarget {
	identity := managedRuntimeIdentity{
		Component: "server", Version: "0.0.0+old", SourceRevision: "old-source", TreeHash: "old-tree",
		ArtifactDigest: strings.Repeat("a", 64), ReleaseID: "mohist-server-old", Generation: 7, IsComplete: true,
	}
	return &managedRuntimeTarget{
		Component: "server", Entrypoint: "/old/Mohist.Server", WorkingDirectory: "/old",
		Arguments: []string{}, RuntimeIdentifier: managedRuntimeIdentifier(), Identity: identity,
		LaunchMode: 0, IsAbsoluteTarget: true, UsesCanonicalEntrypoint: true,
	}
}

func managedBuildTestJSON(value any) json.RawMessage {
	encoded, _ := json.Marshal(value)
	return encoded
}

func containsArg(values []string, want string) bool {
	for _, value := range values {
		if value == want {
			return true
		}
	}
	return false
}

func assertExactJSONKeys(t *testing.T, files *managedBuildTestFiles, path string, want []string) {
	t.Helper()
	value, _, err := files.ReadFile(path)
	if err != nil {
		t.Fatalf("ReadFile(%q) error = %v", path, err)
	}
	var object map[string]json.RawMessage
	if err := json.Unmarshal(value, &object); err != nil {
		t.Fatalf("%s is invalid JSON: %v", path, err)
	}
	got := make([]string, 0, len(object))
	for key := range object {
		got = append(got, key)
	}
	// The JSON object key order is intentionally irrelevant; only spelling and membership are contractual.
	sort.Strings(got)
	wantCopy := append([]string(nil), want...)
	sort.Strings(wantCopy)
	if !reflect.DeepEqual(got, wantCopy) {
		t.Fatalf("%s keys = %#v, want exact keys %#v", path, got, wantCopy)
	}
}

func assertExactNestedJSONKeys(t *testing.T, files *managedBuildTestFiles, path, field string, want []string) {
	t.Helper()
	value, _, err := files.ReadFile(path)
	if err != nil {
		t.Fatalf("ReadFile(%q) error = %v", path, err)
	}
	var object map[string]json.RawMessage
	if err := json.Unmarshal(value, &object); err != nil {
		t.Fatalf("%s is invalid JSON: %v", path, err)
	}
	var nested map[string]json.RawMessage
	if err := json.Unmarshal(object[field], &nested); err != nil {
		t.Fatalf("%s.%s is invalid JSON: %v", path, field, err)
	}
	got := make([]string, 0, len(nested))
	for key := range nested {
		got = append(got, key)
	}
	sort.Strings(got)
	wantCopy := append([]string(nil), want...)
	sort.Strings(wantCopy)
	if !reflect.DeepEqual(got, wantCopy) {
		t.Fatalf("%s.%s keys = %#v, want exact keys %#v", path, field, got, wantCopy)
	}
}

type managedBuildTestFile struct {
	value []byte
	mode  os.FileMode
}

type managedBuildTestFiles struct {
	files     map[string]managedBuildTestFile
	order     []string
	walkOrder map[string][]string
}

func newManagedBuildTestFiles() *managedBuildTestFiles {
	return &managedBuildTestFiles{
		files: make(map[string]managedBuildTestFile), walkOrder: make(map[string][]string),
	}
}

func (fake *managedBuildTestFiles) put(path string, value []byte) {
	if _, exists := fake.files[path]; !exists {
		fake.order = append(fake.order, path)
	}
	fake.files[path] = managedBuildTestFile{value: append([]byte(nil), value...), mode: 0o600}
}

func (fake *managedBuildTestFiles) putJSON(path string, value managedPointer) {
	encoded, _ := json.Marshal(value)
	fake.put(path, encoded)
}

func (fake *managedBuildTestFiles) has(path string) bool {
	_, ok := fake.files[path]
	return ok
}

func (fake *managedBuildTestFiles) Exists(path string) bool {
	if fake.has(path) {
		return true
	}
	prefix := filepath.Clean(path) + string(filepath.Separator)
	for candidate := range fake.files {
		if strings.HasPrefix(candidate, prefix) {
			return true
		}
	}
	return false
}

func (fake *managedBuildTestFiles) ReadFile(path string) ([]byte, os.FileMode, error) {
	file, ok := fake.files[path]
	if !ok {
		return nil, 0, os.ErrNotExist
	}
	return append([]byte(nil), file.value...), file.mode, nil
}

func (fake *managedBuildTestFiles) WriteFileAtomic(path string, value []byte, mode os.FileMode) error {
	if mode == 0 {
		mode = 0o600
	}
	fake.put(path, value)
	file := fake.files[path]
	file.mode = mode
	fake.files[path] = file
	return nil
}

func (fake *managedBuildTestFiles) MkdirAll(string, os.FileMode) error { return nil }

func (fake *managedBuildTestFiles) RemoveAll(path string) error {
	prefix := filepath.Clean(path) + string(filepath.Separator)
	for candidate := range fake.files {
		if candidate == path || strings.HasPrefix(candidate, prefix) {
			delete(fake.files, candidate)
		}
	}
	return nil
}

func (fake *managedBuildTestFiles) Rename(from, to string) error {
	prefix := filepath.Clean(from) + string(filepath.Separator)
	for _, candidate := range append([]struct {
		path string
		file managedBuildTestFile
	}{}, fake.snapshotFiles()...) {
		if candidate.path != from && !strings.HasPrefix(candidate.path, prefix) {
			continue
		}
		relative := strings.TrimPrefix(candidate.path, from)
		fake.put(filepath.Join(to, relative), candidate.file.value)
		fake.files[filepath.Join(to, relative)] = managedBuildTestFile{value: append([]byte(nil), candidate.file.value...), mode: candidate.file.mode}
		delete(fake.files, candidate.path)
	}
	return nil
}

func (fake *managedBuildTestFiles) snapshotFiles() []struct {
	path string
	file managedBuildTestFile
} {
	result := make([]struct {
		path string
		file managedBuildTestFile
	}, 0, len(fake.files))
	for path, file := range fake.files {
		result = append(result, struct {
			path string
			file managedBuildTestFile
		}{path: path, file: file})
	}
	return result
}

func (fake *managedBuildTestFiles) WalkFiles(root string) ([]string, error) {
	if paths, ok := fake.walkOrder[root]; ok {
		return append([]string(nil), paths...), nil
	}
	paths := []string{}
	prefix := filepath.Clean(root) + string(filepath.Separator)
	for _, path := range fake.order {
		if strings.HasPrefix(path, prefix) {
			relative, err := filepath.Rel(root, path)
			if err != nil {
				return nil, err
			}
			paths = append(paths, filepath.ToSlash(relative))
		}
	}
	return paths, nil
}

func (fake *managedBuildTestFiles) OpenLock(string) (io.Closer, error) {
	return managedBuildTestNoopCloser{}, nil
}

type managedBuildTestNoopCloser struct{}

func (managedBuildTestNoopCloser) Close() error { return nil }

type managedBuildTestCommands struct {
	files       *managedBuildTestFiles
	calls       []managedCommand
	failPublish bool
	unitPath    string
}

func newManagedBuildTestCommands(files *managedBuildTestFiles) *managedBuildTestCommands {
	return &managedBuildTestCommands{files: files}
}

func (fake *managedBuildTestCommands) Run(_ context.Context, command managedCommand) managedCommandResult {
	command.Args = append([]string(nil), command.Args...)
	fake.calls = append(fake.calls, command)
	switch command.Name {
	case "npm":
		if len(command.Args) >= 2 && command.Args[0] == "run" && fake.files != nil {
			return managedCommandResult{}
		}
	case "dotnet":
		if fake.failPublish {
			return managedCommandResult{ExitCode: 17, Stderr: "publish details must not escape"}
		}
		for index, value := range command.Args {
			if value == "-o" && index+1 < len(command.Args) {
				fake.files.put(filepath.Join(command.Args[index+1], "Mohist.Server"), []byte("server binary"))
			}
		}
	case "cp":
		if len(command.Args) == 3 {
			fake.copy(command.Args[1], command.Args[2])
		}
	case "sh":
		return managedCommandResult{Stdout: "/usr/bin/node\n"}
	case "git":
		if len(command.Args) >= 2 && command.Args[0] == "rev-parse" && command.Args[1] == "--show-toplevel" {
			return managedCommandResult{Stdout: "/repo\n"}
		}
		if len(command.Args) >= 3 && command.Args[0] == "rev-parse" && command.Args[2] == "HEAD^{tree}" {
			return managedCommandResult{Stdout: managedBuildTestTree + "\n"}
		}
		if len(command.Args) >= 3 && command.Args[0] == "rev-parse" && command.Args[2] == "HEAD" {
			return managedCommandResult{Stdout: managedBuildTestCommit + "\n"}
		}
	case "systemctl":
		return managedBuildTestSystemctlResult(command, fake.unitPath)
	}
	return managedCommandResult{}
}

func (fake *managedBuildTestCommands) copy(from, to string) {
	cleanFrom := filepath.Clean(from)
	for _, candidate := range fake.files.snapshotFiles() {
		path := candidate.path
		file := candidate.file
		if path == cleanFrom {
			fake.files.put(to, file.value)
			continue
		}
		prefix := cleanFrom + string(filepath.Separator)
		if strings.HasPrefix(path, prefix) {
			relative := strings.TrimPrefix(path, prefix)
			fake.files.put(filepath.Join(to, relative), file.value)
		}
	}
}

func managedBuildTestSystemctlResult(command managedCommand, unitPath string) managedCommandResult {
	if len(command.Args) >= 2 {
		switch command.Args[1] {
		case "is-active":
			return managedCommandResult{Stdout: "active\n"}
		case "is-enabled":
			return managedCommandResult{Stdout: "enabled\n"}
		case "show":
			for _, arg := range command.Args {
				if arg == "--property=FragmentPath" {
					return managedCommandResult{Stdout: unitPath + "\n"}
				}
				if arg == "--property=WorkingDirectory" {
					return managedCommandResult{Stdout: "/old\n"}
				}
				if arg == "--property=ExecStart" {
					return managedCommandResult{Stdout: "/old/Mohist.Server\n"}
				}
				if arg == "--property=Environment" {
					return managedCommandResult{Stdout: managedRuntimeIdentityEnvironment + "=/old/runtime-identity.json\n"}
				}
			}
		}
	}
	return managedCommandResult{}
}

type managedBuildTestControl struct {
	beginCalls int
}

func (fake *managedBuildTestControl) ObserveServer(context.Context) (managedRuntimeObservation, error) {
	previous := managedBuildTestServerTarget().Identity
	return managedRuntimeObservation{Identity: previous, Status: "ok"}, nil
}

func (fake *managedBuildTestControl) ObserveRunner(context.Context, string) (managedRuntimeObservation, error) {
	return managedRuntimeObservation{}, errors.New("unexpected runner observation")
}

func (fake *managedBuildTestControl) BeginRunnerInterrupt(context.Context, string, string) (managedRunnerInterrupt, error) {
	fake.beginCalls++
	return managedRunnerInterrupt{}, errors.New("unexpected runner interrupt")
}

func (fake *managedBuildTestControl) CancelRunnerInterrupt(context.Context, string, string) error {
	return errors.New("unexpected runner interrupt cancellation")
}
