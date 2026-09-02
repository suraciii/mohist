package mohistcli

import (
	"archive/tar"
	"bytes"
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

const olderGoUpdaterRevision = "0cdd0a325"

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

func TestCrossRevisionCLIUpdateSynchronizesTargetSkills(t *testing.T) {
	moduleRoot := currentGoModuleRoot(t)
	repoRoot := filepath.Dir(filepath.Dir(filepath.Dir(moduleRoot)))
	fixtureRoot := t.TempDir()
	oldRoot := filepath.Join(fixtureRoot, "old")
	targetRoot := filepath.Join(fixtureRoot, "target")
	archiveRevision(t, repoRoot, olderGoUpdaterRevision, filepath.Join(oldRoot, "packages", "go", "mohist-cli"))
	copyDirectory(t, moduleRoot, filepath.Join(targetRoot, "packages", "go", "mohist-cli"))
	targetSkill := filepath.Join(targetRoot, "packages", "go", "mohist-cli", "skill-data", "target-only", "SKILL.md")
	if err := os.MkdirAll(filepath.Dir(targetSkill), 0o700); err != nil {
		t.Fatal(err)
	}
	writeTestSkillContent(t, targetSkill, "target-revision-marker")

	oldBinary := filepath.Join(fixtureRoot, "old-mo")
	buildCLI(t, filepath.Join(oldRoot, "packages", "go", "mohist-cli"), oldBinary)
	installed := filepath.Join(fixtureRoot, "isolated-install", "mo")
	if err := os.MkdirAll(filepath.Dir(installed), 0o700); err != nil {
		t.Fatal(err)
	}
	copyFile(t, oldBinary, installed)
	oldDigest := fileDigest(t, installed)
	home := filepath.Join(fixtureRoot, "home")
	command := exec.CommandContext(context.Background(), installed, "update", "cli", "--repo-root", targetRoot, "--cli-path", installed)
	command.Env = isolatedEnvironment(home)
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("old updater failed: %v\n%s", err, output)
	}

	if newDigest := fileDigest(t, installed); newDigest == oldDigest {
		t.Fatalf("installed binary digest did not change: %s", newDigest)
	}
	verify := exec.CommandContext(context.Background(), installed, "--help")
	verify.Env = isolatedEnvironment(home)
	if output, err := verify.CombinedOutput(); err != nil {
		t.Fatalf("replacement binary failed: %v\n%s", err, output)
	}
	marker, err := os.ReadFile(filepath.Join(home, ".mohist", "cli", "skill-data", "target-only", "SKILL.md"))
	if err != nil {
		t.Fatalf("target-only skill was not synchronized: %v", err)
	}
	if !strings.Contains(string(marker), "target-revision-marker") {
		t.Fatalf("target-only skill marker missing from managed data: %s", marker)
	}
}

func currentGoModuleRoot(t *testing.T) string {
	t.Helper()
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("could not locate maintenance test")
	}
	return filepath.Dir(file)
}

func archiveRevision(t *testing.T, repoRoot, revision, destination string) {
	t.Helper()
	if err := os.MkdirAll(destination, 0o700); err != nil {
		t.Fatal(err)
	}
	command := exec.Command("git", "archive", "--format=tar", revision, "packages/go/mohist-cli")
	command.Dir = repoRoot
	archive, err := command.Output()
	if err != nil {
		t.Fatalf("archive updater revision %s: %v", revision, err)
	}
	reader := tar.NewReader(bytes.NewReader(archive))
	for {
		header, err := reader.Next()
		if errors.Is(err, io.EOF) {
			return
		}
		if err != nil {
			t.Fatal(err)
		}
		relative := strings.TrimPrefix(header.Name, "packages/go/mohist-cli/")
		path := filepath.Join(destination, relative)
		switch header.Typeflag {
		case tar.TypeDir:
			if err := os.MkdirAll(path, 0o700); err != nil {
				t.Fatal(err)
			}
		case tar.TypeReg:
			if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
				t.Fatal(err)
			}
			data, err := io.ReadAll(reader)
			if err != nil {
				t.Fatal(err)
			}
			if err := os.WriteFile(path, data, 0o600); err != nil {
				t.Fatal(err)
			}
		}
	}
}

func copyDirectory(t *testing.T, source, destination string) {
	t.Helper()
	err := filepath.Walk(source, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		relative, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		target := filepath.Join(destination, relative)
		if info.IsDir() {
			return os.MkdirAll(target, 0o700)
		}
		return copyFile(t, path, target)
	})
	if err != nil {
		t.Fatal(err)
	}
}

func copyFile(t *testing.T, source, destination string) error {
	t.Helper()
	data, err := os.ReadFile(source)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(destination), 0o700); err != nil {
		return err
	}
	if err := os.WriteFile(destination, data, 0o700); err != nil {
		return err
	}
	return nil
}

func buildCLI(t *testing.T, moduleRoot, output string) {
	t.Helper()
	command := exec.Command("go", "-C", moduleRoot, "build", "-tags", "netgo,osusergo", "-trimpath", "-buildvcs=false", "-o", output, "./cmd/mo")
	command.Env = isolatedEnvironment(filepath.Join(filepath.Dir(output), "build-home"))
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("build %s: %v\n%s", moduleRoot, err, output)
	}
}

func fileDigest(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return fmt.Sprintf("%x", sha256.Sum256(data))
}

func isolatedEnvironment(home string) []string {
	result := []string{}
	for _, value := range os.Environ() {
		if strings.HasPrefix(value, "HOME=") || strings.HasPrefix(value, "XDG_CONFIG_HOME=") || strings.HasPrefix(value, "XDG_DATA_HOME=") || strings.HasPrefix(value, "MOHIST_SKILLS_DIR=") || strings.HasPrefix(value, "GOCACHE=") {
			continue
		}
		result = append(result, value)
	}
	return append(result, "HOME="+home, "XDG_CONFIG_HOME="+filepath.Join(home, ".config"), "XDG_DATA_HOME="+filepath.Join(home, ".local", "share"), "GOCACHE="+filepath.Join(home, ".cache", "go-build"))
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
