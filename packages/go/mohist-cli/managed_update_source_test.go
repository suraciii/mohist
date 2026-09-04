package mohistcli

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

var (
	managedSourceTestCommit = strings.Repeat("a", 40)
	managedSourceTestTree   = strings.Repeat("b", 40)
)

func TestResolveManagedRepositoryRootRejectsExplicitTopLevelMismatch(t *testing.T) {
	commands := &managedSourceFakeCommands{
		t:       t,
		results: []managedCommandResult{{Stdout: "/actual/repository\n"}},
	}
	files := newManagedSourceFakeFiles("/actual/repository/Mohist.sln")

	_, err := resolveManagedRepositoryRoot(
		context.Background(),
		managedUpdateEnvironment{commands: commands, files: files},
		"/requested/repository",
	)

	if err == nil || !strings.Contains(err.Error(), "is not the Git top-level") {
		t.Fatalf("error = %v, want Git top-level mismatch", err)
	}
	commands.assertCallCount(1)
}

func TestCaptureManagedSourceRejectsGitInspectionFailuresWithoutLeakingStderr(t *testing.T) {
	tests := []struct {
		name    string
		results []managedCommandResult
		want    string
	}{
		{
			name:    "not a Git repository",
			results: []managedCommandResult{{ExitCode: 128, Stderr: "fatal: secret repository detail"}},
			want:    "source Git top-level discovery failed with exit code 128",
		},
		{
			name: "HEAD inspection",
			results: []managedCommandResult{
				{Stdout: "/repo\n"},
				{ExitCode: 3, Stderr: "secret HEAD detail"},
			},
			want: "source Git commit inspection failed with exit code 3",
		},
		{
			name: "tree inspection",
			results: []managedCommandResult{
				{Stdout: "/repo\n"},
				{Stdout: managedSourceTestCommit + "\n"},
				{ExitCode: 4, Stderr: "secret tree detail"},
			},
			want: "source Git tree inspection failed with exit code 4",
		},
		{
			name: "status inspection",
			results: []managedCommandResult{
				{Stdout: "/repo\n"},
				{Stdout: managedSourceTestCommit + "\n"},
				{Stdout: managedSourceTestTree + "\n"},
				{ExitCode: 5, Stderr: "secret status detail"},
			},
			want: "source Git cleanliness inspection failed with exit code 5",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			commands := &managedSourceFakeCommands{t: t, results: test.results}
			files := newManagedSourceFakeFiles("/repo/Mohist.sln")

			_, err := captureManagedSource(
				context.Background(),
				managedUpdateEnvironment{commands: commands, files: files},
				"/repo",
				"/runtime",
				"tx-failed-git",
			)

			if err == nil || err.Error() != test.want {
				t.Fatalf("error = %v, want %q", err, test.want)
			}
			if strings.Contains(err.Error(), "secret") {
				t.Fatalf("error leaked command stderr: %q", err)
			}
			if len(files.mkdirCalls) != 0 {
				t.Fatalf("workspace directories created before source validation: %#v", files.mkdirCalls)
			}
			commands.assertCallCount(len(test.results))
		})
	}
}

func TestCaptureManagedSourceRejectsTrackedAndUntrackedDirt(t *testing.T) {
	tests := []struct {
		name   string
		status string
	}{
		{name: "tracked", status: " M packages/go/mohist-cli/maintenance.go\n"},
		{name: "untracked", status: "?? local-secret.env\n"},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			commands := &managedSourceFakeCommands{
				t: t,
				results: []managedCommandResult{
					{Stdout: "/repo\n"},
					{Stdout: managedSourceTestCommit + "\n"},
					{Stdout: managedSourceTestTree + "\n"},
					{Stdout: test.status},
				},
			}
			files := newManagedSourceFakeFiles("/repo/Mohist.sln")

			_, err := captureManagedSource(
				context.Background(),
				managedUpdateEnvironment{commands: commands, files: files},
				"/repo",
				"/runtime",
				"tx-dirty",
			)

			if err == nil || !strings.Contains(err.Error(), "is dirty") {
				t.Fatalf("error = %v, want dirty source rejection", err)
			}
			if strings.Contains(err.Error(), strings.TrimSpace(test.status)) {
				t.Fatalf("error leaked porcelain output: %q", err)
			}
			if len(files.mkdirCalls) != 0 {
				t.Fatalf("workspace directories created for dirty source: %#v", files.mkdirCalls)
			}
			commands.assertCallCount(4)
		})
	}
}

func TestParseManagedObjectIDAcceptsOnlyStrictSHA1OrSHA256Hex(t *testing.T) {
	tests := []struct {
		name    string
		value   string
		want    string
		wantErr bool
	}{
		{name: "SHA-1", value: strings.Repeat("A", 40) + "\n", want: strings.Repeat("a", 40)},
		{name: "SHA-256", value: strings.Repeat("b", 64) + "\n", want: strings.Repeat("b", 64)},
		{name: "empty", value: "\n", wantErr: true},
		{name: "short SHA-1", value: strings.Repeat("a", 39), wantErr: true},
		{name: "long SHA-1", value: strings.Repeat("a", 41), wantErr: true},
		{name: "short SHA-256", value: strings.Repeat("a", 63), wantErr: true},
		{name: "long SHA-256", value: strings.Repeat("a", 65), wantErr: true},
		{name: "non hex", value: strings.Repeat("g", 40), wantErr: true},
		{name: "multiple lines", value: strings.Repeat("a", 40) + "\n" + strings.Repeat("b", 40), wantErr: true},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, err := parseManagedObjectID(test.value, "test identity")
			if test.wantErr {
				if err == nil {
					t.Fatalf("parseManagedObjectID(%q) succeeded with %q", test.value, got)
				}
				return
			}
			if err != nil || got != test.want {
				t.Fatalf("parseManagedObjectID(%q) = %q, %v; want %q", test.value, got, err, test.want)
			}
		})
	}
}

func TestCaptureManagedSourceArchivesOnceExtractsTwiceAndProtectsSnapshot(t *testing.T) {
	transactionRoot := filepath.Join("/runtime", "transactions", "tx-success")
	buildContainerRoot := filepath.Join(transactionRoot, "build")
	snapshotRoot := filepath.Join(transactionRoot, "snapshot")
	buildRoot := filepath.Join(buildContainerRoot, "source")
	archivePath := filepath.Join(buildContainerRoot, "source.tar")
	commands := &managedSourceFakeCommands{
		t: t,
		results: []managedCommandResult{
			{Stdout: "/repo\n"},
			{Stdout: managedSourceTestCommit + "\n"},
			{Stdout: managedSourceTestTree + "\n"},
			{},
			{},
			{},
			{},
			{},
		},
	}
	files := newManagedSourceFakeFiles("/repo/Mohist.sln")

	source, err := captureManagedSource(
		context.Background(),
		managedUpdateEnvironment{commands: commands, files: files},
		"/repo",
		"/runtime",
		"tx-success",
	)

	if err != nil {
		t.Fatalf("captureManagedSource() error = %v", err)
	}
	wantSource := managedSource{
		RepositoryRoot: "/repo",
		Commit:         managedSourceTestCommit,
		TreeHash:       managedSourceTestTree,
		SnapshotRoot:   filepath.ToSlash(snapshotRoot),
		BuildRoot:      filepath.ToSlash(buildRoot),
	}
	if !reflect.DeepEqual(source, wantSource) {
		t.Fatalf("source = %#v, want %#v", source, wantSource)
	}
	wantMkdirCalls := []managedSourceMkdirCall{
		{path: transactionRoot, mode: 0o700},
		{path: snapshotRoot, mode: 0o700},
		{path: buildContainerRoot, mode: 0o700},
		{path: buildRoot, mode: 0o700},
	}
	if !reflect.DeepEqual(files.mkdirCalls, wantMkdirCalls) {
		t.Fatalf("MkdirAll calls = %#v, want %#v", files.mkdirCalls, wantMkdirCalls)
	}
	wantCommands := []managedCommand{
		{Name: "git", Args: []string{"rev-parse", "--show-toplevel"}, Dir: "/repo"},
		{Name: "git", Args: []string{"rev-parse", "--verify", "HEAD"}, Dir: "/repo"},
		{Name: "git", Args: []string{"rev-parse", "--verify", "HEAD^{tree}"}, Dir: "/repo"},
		{Name: "git", Args: []string{"status", "--porcelain", "--untracked-files=all"}, Dir: "/repo"},
		{Name: "git", Args: []string{"archive", "--format=tar", "--output=" + archivePath, managedSourceTestCommit}, Dir: "/repo"},
		{Name: "tar", Args: []string{"-xf", archivePath, "-C", snapshotRoot}, Dir: buildContainerRoot},
		{Name: "tar", Args: []string{"-xf", archivePath, "-C", buildRoot}, Dir: buildContainerRoot},
		{Name: "chmod", Args: []string{"-R", "a-w", snapshotRoot}, Dir: transactionRoot},
	}
	if !reflect.DeepEqual(commands.calls, wantCommands) {
		t.Fatalf("commands = %#v, want %#v", commands.calls, wantCommands)
	}
	commands.assertCallCount(len(wantCommands))
}

func TestVerifyManagedSourceUnchangedRejectsPostBuildChanges(t *testing.T) {
	changedCommit := strings.Repeat("c", 40)
	changedTree := strings.Repeat("d", 40)
	tests := []struct {
		name   string
		commit string
		tree   string
		status managedCommandResult
		want   string
	}{
		{
			name: "HEAD changed", commit: changedCommit, tree: managedSourceTestTree,
			status: managedCommandResult{}, want: "source changed after target capture",
		},
		{
			name: "tree changed", commit: managedSourceTestCommit, tree: changedTree,
			status: managedCommandResult{}, want: "source changed after target capture",
		},
		{
			name: "worktree became dirty", commit: managedSourceTestCommit, tree: managedSourceTestTree,
			status: managedCommandResult{Stdout: "?? generated-after-build.txt\n"}, want: "is dirty",
		},
		{
			name: "status can no longer be read", commit: managedSourceTestCommit, tree: managedSourceTestTree,
			status: managedCommandResult{ExitCode: 7, Stderr: "secret post-build status detail"}, want: "source identity could not be read back after staging",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			commands := &managedSourceFakeCommands{
				t: t,
				results: []managedCommandResult{
					{Stdout: "/repo\n"},
					{Stdout: test.commit + "\n"},
					{Stdout: test.tree + "\n"},
					test.status,
				},
			}
			files := newManagedSourceFakeFiles("/repo/Mohist.sln")
			source := managedSource{
				RepositoryRoot: "/repo",
				Commit:         managedSourceTestCommit,
				TreeHash:       managedSourceTestTree,
			}

			err := verifyManagedSourceUnchanged(
				context.Background(),
				managedUpdateEnvironment{commands: commands, files: files},
				source,
			)

			if err == nil || !strings.Contains(err.Error(), test.want) {
				t.Fatalf("error = %v, want text %q", err, test.want)
			}
			if strings.Contains(err.Error(), "secret") || strings.Contains(err.Error(), "generated-after-build.txt") {
				t.Fatalf("error leaked command output: %q", err)
			}
			commands.assertCallCount(4)
		})
	}
}

func TestVerifyManagedSourceUnchangedAcceptsMatchingCleanSource(t *testing.T) {
	commands := &managedSourceFakeCommands{
		t: t,
		results: []managedCommandResult{
			{Stdout: "/repo\n"},
			{Stdout: managedSourceTestCommit + "\n"},
			{Stdout: managedSourceTestTree + "\n"},
			{},
		},
	}
	files := newManagedSourceFakeFiles("/repo/Mohist.sln")

	err := verifyManagedSourceUnchanged(
		context.Background(),
		managedUpdateEnvironment{commands: commands, files: files},
		managedSource{RepositoryRoot: "/repo", Commit: managedSourceTestCommit, TreeHash: managedSourceTestTree},
	)

	if err != nil {
		t.Fatalf("verifyManagedSourceUnchanged() error = %v", err)
	}
	commands.assertCallCount(4)
}

type managedSourceFakeCommands struct {
	t       testing.TB
	results []managedCommandResult
	calls   []managedCommand
}

func (fake *managedSourceFakeCommands) Run(_ context.Context, command managedCommand) managedCommandResult {
	fake.t.Helper()
	index := len(fake.calls)
	command.Args = append([]string(nil), command.Args...)
	fake.calls = append(fake.calls, command)
	if index >= len(fake.results) {
		fake.t.Fatalf("unexpected command %s %#v in %q", command.Name, command.Args, command.Dir)
	}
	return fake.results[index]
}

func (fake *managedSourceFakeCommands) assertCallCount(want int) {
	fake.t.Helper()
	if len(fake.calls) != want {
		fake.t.Fatalf("command calls = %d, want %d: %#v", len(fake.calls), want, fake.calls)
	}
}

type managedSourceMkdirCall struct {
	path string
	mode os.FileMode
}

type managedSourceFakeFiles struct {
	existing   map[string]bool
	mkdirCalls []managedSourceMkdirCall
}

func newManagedSourceFakeFiles(paths ...string) *managedSourceFakeFiles {
	existing := make(map[string]bool, len(paths))
	for _, path := range paths {
		existing[path] = true
	}
	return &managedSourceFakeFiles{existing: existing}
}

func (fake *managedSourceFakeFiles) Exists(path string) bool {
	return fake.existing[path]
}

func (fake *managedSourceFakeFiles) ReadFile(string) ([]byte, os.FileMode, error) {
	return nil, 0, errors.New("unexpected ReadFile")
}

func (fake *managedSourceFakeFiles) WriteFileAtomic(string, []byte, os.FileMode) error {
	return errors.New("unexpected WriteFileAtomic")
}

func (fake *managedSourceFakeFiles) MkdirAll(path string, mode os.FileMode) error {
	fake.mkdirCalls = append(fake.mkdirCalls, managedSourceMkdirCall{path: path, mode: mode})
	fake.existing[path] = true
	return nil
}

func (fake *managedSourceFakeFiles) RemoveAll(string) error {
	return errors.New("unexpected RemoveAll")
}

func (fake *managedSourceFakeFiles) Rename(string, string) error {
	return errors.New("unexpected Rename")
}

func (fake *managedSourceFakeFiles) WalkFiles(string) ([]string, error) {
	return nil, errors.New("unexpected WalkFiles")
}

func (fake *managedSourceFakeFiles) OpenLock(string) (io.Closer, error) {
	return nil, errors.New("unexpected OpenLock")
}
