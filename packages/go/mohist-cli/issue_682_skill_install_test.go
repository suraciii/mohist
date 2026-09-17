package mohistcli

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func skillInstallDeps(t *testing.T, skillsRoot, cwd, home string, env map[string]string) (Dependencies, *bytes.Buffer, *bytes.Buffer) {
	t.Helper()
	var stdout, stderr bytes.Buffer
	return Dependencies{
		Stdout: &stdout,
		Stderr: &stderr,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_SKILLS_DIR" {
				return skillsRoot, true
			}
			if value, ok := env[name]; ok {
				return value, true
			}
			return "", false
		},
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return cwd },
		Executable:       func() string { return filepath.Join(cwd, "mo") },
	}, &stdout, &stderr
}

// TestIssue682SkillInstallClaudeWritesClaudeTarget proves --claude is accepted
// on the install action reach installSkillStub and writes the .claude/skills
// target before exiting cleanly.
func TestIssue682SkillInstallClaudeWritesClaudeTarget(t *testing.T) {
	skillsRoot := t.TempDir()
	writeTestSkill(t, skillsRoot, "mohist")
	cwd := t.TempDir()
	deps, stdout, stderr := skillInstallDeps(t, skillsRoot, cwd, t.TempDir(), nil)
	if code := Run(context.Background(), []string{"skill", "install", "--claude"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, stdout.String(), stderr.String())
	}
	target := filepath.Join(cwd, ".claude", "skills", "mohist", "SKILL.md")
	info, err := os.Stat(target)
	if err != nil {
		t.Fatalf("claude target %s not written: %v", target, err)
	}
	if info.Mode().Perm() != 0o600 {
		t.Fatalf("mode=%o want 0600", info.Mode().Perm())
	}
}

// TestIssue682SkillInstallHermesWritesHermesHomeTarget proves --hermes is
// accepted on the install action and writes the Hermes home skills target.
func TestIssue682SkillInstallHermesWritesHermesHomeTarget(t *testing.T) {
	skillsRoot := t.TempDir()
	writeTestSkill(t, skillsRoot, "mohist")
	hermesHome := t.TempDir()
	deps, stdout, stderr := skillInstallDeps(t, skillsRoot, t.TempDir(), t.TempDir(), map[string]string{"HERMES_HOME": hermesHome})
	if code := Run(context.Background(), []string{"skill", "install", "--hermes"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, stdout.String(), stderr.String())
	}
	target := filepath.Join(hermesHome, "skills", "mohist", "SKILL.md")
	if _, err := os.Stat(target); err != nil {
		t.Fatalf("hermes target %s not written: %v", target, err)
	}
}

// TestIssue682SkillInstallConflictsFailLocallyBeforeWrite pins that the
// existing installSkillStub conflict rule still runs before any filesystem
// change: --claude with --hermes and --hermes with --path both fail locally.
func TestIssue682SkillInstallConflictsFailLocallyBeforeWrite(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{name: "claude and hermes", args: []string{"skill", "install", "--claude", "--hermes"}},
		{name: "hermes and path", args: []string{"skill", "install", "--hermes", "--path", "/tmp/mohist-skill-conflict"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			skillsRoot := t.TempDir()
			writeTestSkill(t, skillsRoot, "mohist")
			deps, _, stderr := skillInstallDeps(t, skillsRoot, t.TempDir(), t.TempDir(), nil)
			writes := 0
			deps.WriteFile = func(string, string, os.FileMode) error { writes++; return nil }
			deps.MkdirAll = func(string, os.FileMode) error { writes++; return nil }
			code := Run(context.Background(), tc.args, deps)
			if code != ExitOperation {
				t.Fatalf("code=%d stderr=%q", code, stderr.String())
			}
			if writes != 0 {
				t.Fatalf("conflict performed %d filesystem writes", writes)
			}
			if !strings.Contains(stderr.String(), "--hermes cannot be combined with --claude or --path") {
				t.Fatalf("stderr=%q", stderr.String())
			}
		})
	}
}

// TestIssue682SkillNonInstallActionsRejectClaudeHermes proves the new boolean
// cases are scoped to install: every other skill action treats --claude and
// --hermes as usage errors.
func TestIssue682SkillNonInstallActionsRejectClaudeHermes(t *testing.T) {
	cases := [][]string{
		{"skill", "list", "--claude"},
		{"skill", "view", "mohist", "--hermes"},
		{"skill", "path", "mohist", "--claude"},
		{"skill", "sync", "--hermes"},
	}
	for _, args := range cases {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			skillsRoot := t.TempDir()
			writeTestSkill(t, skillsRoot, "mohist")
			deps, _, stderr := skillInstallDeps(t, skillsRoot, t.TempDir(), t.TempDir(), nil)
			writes := 0
			deps.WriteFile = func(string, string, os.FileMode) error { writes++; return nil }
			if code := Run(context.Background(), args, deps); code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, stderr.String())
			}
			if writes != 0 {
				t.Fatalf("rejected action performed %d writes", writes)
			}
			if !strings.Contains(stderr.String(), "only valid with mo skill install") {
				t.Fatalf("stderr=%q", stderr.String())
			}
		})
	}
}

// TestIssue682SkillInstallHelpUnchanged pins the existing install usage line.
func TestIssue682SkillInstallHelpUnchanged(t *testing.T) {
	deps, stdout, stderr := skillInstallDeps(t, t.TempDir(), t.TempDir(), t.TempDir(), nil)
	if code := Run(context.Background(), []string{"skill", "install", "--help"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, stderr.String())
	}
	if !strings.Contains(stdout.String(), "mo skill install [--path <path>] [--claude] [--hermes]") {
		t.Fatalf("stdout=%q", stdout.String())
	}
}
