package mohistcli

import (
	"context"
	"encoding/json"
	"io"
	"io/fs"
	"os"
	"time"
)

type ManagedUpdateRequest struct {
	Components []string
	RepoRoot   string
	UnitDir    string
	DryRun     bool
}

type ManagedUpdateRuntime interface {
	Update(context.Context, ManagedUpdateRequest) error
}

type managedCommand struct {
	Name string
	Args []string
	Dir  string
}

type managedCommandResult struct {
	ExitCode int
	Stdout   string
	Stderr   string
}

type managedCommandRunner interface {
	Run(context.Context, managedCommand) managedCommandResult
}

type managedFileSystem interface {
	Exists(string) bool
	ReadFile(string) ([]byte, os.FileMode, error)
	WriteFileAtomic(string, []byte, os.FileMode) error
	MkdirAll(string, os.FileMode) error
	RemoveAll(string) error
	Rename(string, string) error
	WalkFiles(string) ([]string, error)
	OpenLock(string) (io.Closer, error)
}

type managedControlPlane interface {
	ObserveServer(context.Context) (managedRuntimeObservation, error)
	ObserveRunner(context.Context, string) (managedRuntimeObservation, error)
	BeginRunnerInterrupt(context.Context, string, string) (managedRunnerInterrupt, error)
	CancelRunnerInterrupt(context.Context, string, string) error
}

type managedUpdateEnvironment struct {
	files    managedFileSystem
	commands managedCommandRunner
	control  managedControlPlane
	now      func() time.Time
	wait     func(context.Context, time.Duration) error
	newID    func() string
	homeDir  func() (string, error)
	stdout   io.Writer
	stderr   io.Writer
}

type managedSource struct {
	RepositoryRoot string `json:"repositoryRoot"`
	Commit         string `json:"commit"`
	TreeHash       string `json:"treeHash"`
	SnapshotRoot   string `json:"snapshotRoot"`
	BuildRoot      string `json:"buildRoot"`
}

type managedRuntimeIdentity struct {
	Component            string `json:"component"`
	Version              string `json:"version"`
	SourceRevision       string `json:"sourceRevision"`
	TreeHash             string `json:"treeHash"`
	ArtifactDigest       string `json:"artifactDigest"`
	ReleaseID            string `json:"releaseId"`
	Generation           int64  `json:"generation"`
	RunnerID             string `json:"runnerId,omitempty"`
	ConnectionGeneration string `json:"connectionGeneration,omitempty"`
	BuildGitHash         string `json:"buildGitHash,omitempty"`
	IsComplete           bool   `json:"isComplete"`
}

type managedRuntimeTarget struct {
	Component               string                 `json:"component"`
	Entrypoint              string                 `json:"entrypoint"`
	WorkingDirectory        string                 `json:"workingDirectory"`
	Arguments               []string               `json:"arguments"`
	RuntimeIdentifier       string                 `json:"runtimeIdentifier"`
	Identity                managedRuntimeIdentity `json:"identity"`
	NodeExecutable          *string                `json:"nodeExecutable"`
	DependencyRoot          *string                `json:"dependencyRoot"`
	LaunchMode              int                    `json:"launchMode"`
	IsAbsoluteTarget        bool                   `json:"isAbsoluteTarget"`
	UsesCanonicalEntrypoint bool                   `json:"usesCanonicalEntrypoint"`
}

type managedRuntimeObservation struct {
	Identity             managedRuntimeIdentity
	Status               string
	ConnectionState      string
	ConnectionGeneration string
}

type managedRunnerInterrupt struct {
	RunnerID        string
	InterruptID     string
	Status          string
	ActiveWorkCount int
}

type managedServiceSnapshot struct {
	Component      string                `json:"component"`
	UnitPath       string                `json:"unitPath"`
	UnitSnapshot   string                `json:"unitSnapshot"`
	UnitMode       os.FileMode           `json:"unitMode"`
	WasActive      bool                  `json:"wasActive"`
	WasEnabled     bool                  `json:"wasEnabled"`
	PreviousTarget *managedRuntimeTarget `json:"previousTarget,omitempty"`
}

type managedTransaction struct {
	ID            string                             `json:"id"`
	Status        string                             `json:"status"`
	Generation    int64                              `json:"generation"`
	UnitDirectory string                             `json:"unitDirectory"`
	Source        managedSource                      `json:"source"`
	Targets       map[string]*managedRuntimeTarget   `json:"targets"`
	Services      map[string]*managedServiceSnapshot `json:"services"`
	InterruptID   string                             `json:"interruptId,omitempty"`
	Failure       string                             `json:"failure,omitempty"`
	CreatedAt     time.Time                          `json:"createdAt"`
	UpdatedAt     time.Time                          `json:"updatedAt"`
}

type managedPointer map[string]json.RawMessage

type realManagedFiles struct{}

func (realManagedFiles) Exists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func (realManagedFiles) ReadFile(path string) ([]byte, os.FileMode, error) {
	value, err := os.ReadFile(path)
	if err != nil {
		return nil, 0, err
	}
	info, err := os.Stat(path)
	if err != nil {
		return nil, 0, err
	}
	return value, info.Mode(), nil
}

func (realManagedFiles) MkdirAll(path string, mode os.FileMode) error {
	return os.MkdirAll(path, mode)
}

func (realManagedFiles) RemoveAll(path string) error  { return os.RemoveAll(path) }
func (realManagedFiles) Rename(from, to string) error { return os.Rename(from, to) }

func (realManagedFiles) WalkFiles(root string) ([]string, error) {
	paths := []string{}
	err := fs.WalkDir(os.DirFS(root), ".", func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if path == "." || entry.IsDir() {
			return nil
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if !info.Mode().IsRegular() {
			return &fs.PathError{Op: "walk", Path: path, Err: fs.ErrInvalid}
		}
		paths = append(paths, path)
		return nil
	})
	return paths, err
}
