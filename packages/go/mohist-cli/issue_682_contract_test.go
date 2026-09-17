package mohistcli

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

// TestIssue682ShortFlagAllowlistIsClosed pins the one table plus the one
// canonicalization helper: the allowlist maps every declared short flag 1:1 to
// its long spelling, and every other token is returned untouched. No
// abbreviation logic exists.
func TestIssue682ShortFlagAllowlistIsClosed(t *testing.T) {
	want := map[string]string{
		"-l": "--label",
		"-m": "--message",
		"-p": "--priority",
		"-y": "--yes",
		"-b": "--body",
		"-f": "--follow",
		"-n": "--lines",
		"-v": "--verbose",
	}
	if !reflect.DeepEqual(shortFlagLongForm, want) {
		t.Fatalf("shortFlagLongForm=%v want=%v", shortFlagLongForm, want)
	}
	for short, long := range want {
		if got := canonicalFlag(short); got != long {
			t.Errorf("canonicalFlag(%q)=%q want %q", short, got, long)
		}
	}
	for _, arg := range []string{"--label", "-x", "value", "", "-", "--help", "-h", "--json"} {
		if got := canonicalFlag(arg); got != arg {
			t.Errorf("canonicalFlag(%q)=%q want unchanged", arg, got)
		}
	}
}

// TestIssue682ShortFlagsEqualLongFlags parses the short and long spelling of
// each allowlisted T-001 flag on its declaring leaf and requires identical
// command.args, so the short form cannot drift from the long form.
func TestIssue682ShortFlagsEqualLongFlags(t *testing.T) {
	cases := []struct {
		name  string
		long  []string
		short []string
	}{
		{
			name:  "issue list priority",
			long:  []string{"issue", "list", "--project", "proj", "--priority", "p1"},
			short: []string{"issue", "list", "--project", "proj", "-p", "p1"},
		},
		{
			name:  "issue create label",
			long:  []string{"issue", "create", "Title", "--body", "b", "--project", "proj", "--label", "a=b"},
			short: []string{"issue", "create", "Title", "--body", "b", "--project", "proj", "-l", "a=b"},
		},
		{
			name:  "issue create body and priority",
			long:  []string{"issue", "create", "Title", "--body", "b", "--priority", "p1", "--project", "proj"},
			short: []string{"issue", "create", "Title", "-b", "b", "-p", "p1", "--project", "proj"},
		},
		{
			name:  "issue edit label and priority",
			long:  []string{"issue", "edit", "42", "--project", "proj", "--title", "T", "--label", "a=b", "--priority", "p1"},
			short: []string{"issue", "edit", "42", "--project", "proj", "--title", "T", "-l", "a=b", "-p", "p1"},
		},
		{
			name:  "issue edit body",
			long:  []string{"issue", "edit", "42", "--project", "proj", "--body", "b"},
			short: []string{"issue", "edit", "42", "--project", "proj", "-b", "b"},
		},
		{
			name:  "epic create priority",
			long:  []string{"epic", "create", "Title", "--project", "proj", "--description", "d", "--priority", "p2"},
			short: []string{"epic", "create", "Title", "--project", "proj", "--description", "d", "-p", "p2"},
		},
		{
			name:  "epic edit priority",
			long:  []string{"epic", "edit", "7", "--project", "proj", "--title", "T", "--priority", "p2"},
			short: []string{"epic", "edit", "7", "--project", "proj", "--title", "T", "-p", "p2"},
		},
		{
			name:  "issue comment create body",
			long:  []string{"issue", "comment", "create", "42", "--project", "proj", "--body", "hi"},
			short: []string{"issue", "comment", "create", "42", "--project", "proj", "-b", "hi"},
		},
		{
			name:  "project workflow prompt set body",
			long:  []string{"project", "workflow", "prompt", "set", "key", "--project", "proj", "--body", "x"},
			short: []string{"project", "workflow", "prompt", "set", "key", "--project", "proj", "-b", "x"},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			long, err := parse(tc.long)
			if err != nil {
				t.Fatalf("parse(long %v): %v", tc.long, err)
			}
			short, err := parse(tc.short)
			if err != nil {
				t.Fatalf("parse(short %v): %v", tc.short, err)
			}
			if long.kind != short.kind {
				t.Fatalf("kind long=%q short=%q", long.kind, short.kind)
			}
			if !reflect.DeepEqual(long.args, short.args) {
				t.Fatalf("args differ: long=%v short=%v", long.args, short.args)
			}
		})
	}
}

// TestIssue682CreateShortLabelsFormsLabels drives the shipped
// `issue create Title --body-file f -l key=value` form end to end and asserts
// the POST body carries the labels object built from the short flags.
func TestIssue682CreateShortLabelsFormsLabels(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{
			name: "body-file",
			args: []string{"issue", "create", "Title", "--body-file", "f", "-l", "team=core", "-l", "kind=bug"},
		},
		{
			name: "inline body",
			args: []string{"issue", "create", "Title", "--body", "b", "-l", "team=core", "-l", "kind=bug"},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var gotMethod, gotPath string
			var gotBody []byte
			deps, out, errOut := organizationDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
				gotMethod = r.Method
				gotPath = r.URL.Path
				gotBody, _ = io.ReadAll(r.Body)
				return response(http.StatusOK, `{"success":true,"data":{"number":1,"title":"Title"}}`), nil
			}))
			if code := Run(context.Background(), tc.args, deps); code != ExitOK {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if gotMethod != http.MethodPost || gotPath != "/api/projects/proj/issues" {
				t.Fatalf("request method=%q path=%q", gotMethod, gotPath)
			}
			var payload map[string]any
			if err := json.Unmarshal(gotBody, &payload); err != nil {
				t.Fatalf("body %q is not JSON: %v", gotBody, err)
			}
			labels, ok := payload["labels"].(map[string]any)
			if !ok || labels["team"] != "core" || labels["kind"] != "bug" {
				t.Fatalf("labels=%v body=%s", payload["labels"], gotBody)
			}
			cmd, err := parse(tc.args)
			if err != nil {
				t.Fatalf("parse(%v): %v", tc.args, err)
			}
			for _, flag := range []string{"-l", "--label", "-b", "--body"} {
				if contains(cmd.args, flag) {
					t.Fatalf("long/short flag leaked into args: %v", cmd.args)
				}
			}
			if values := valuesFor(cmd.args, "label"); len(values) != 2 {
				t.Fatalf("label values=%v", values)
			}
		})
	}
}

// TestIssue682ShortFlagValuesAreNeverRewritten proves values are read
// positionally and never canonicalized: a value that looks like a short flag
// survives byte for byte.
func TestIssue682ShortFlagValuesAreNeverRewritten(t *testing.T) {
	cases := []struct {
		name string
		args []string
		key  string
		want string
	}{
		{
			name: "long body value looks like short flag",
			args: []string{"issue", "create", "Title", "--body", "-l", "--project", "proj"},
			key:  "body",
			want: "-l",
		},
		{
			name: "short label value looks like short flag",
			args: []string{"issue", "create", "Title", "--body", "b", "-l", "-x", "--project", "proj"},
			key:  "label",
			want: "-x",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			cmd, err := parse(tc.args)
			if err != nil {
				t.Fatalf("parse(%v): %v", tc.args, err)
			}
			if got := argValue(cmd.args, tc.key, ""); got != tc.want {
				t.Fatalf("argValue(%q)=%q want %q (args=%v)", tc.key, got, tc.want, cmd.args)
			}
		})
	}
}

// TestIssue682ShortValueFlagBeforeDiscoveryKeepsValue mirrors
// TestDiscoveryPreservesOptionValues for the new short spellings: the token
// after a short value flag is a value, not a discovery request.
func TestIssue682ShortValueFlagBeforeDiscoveryKeepsValue(t *testing.T) {
	for _, token := range []string{"--help", "--json"} {
		for _, tc := range []struct {
			name string
			args []string
			key  string
		}{
			{name: "issue label", args: []string{"issue", "create", "Title", "--body", "b", "-l", token}, key: "label"},
			{name: "issue body", args: []string{"issue", "create", "Title", "-b", token, "--project", "proj"}, key: "body"},
		} {
			t.Run(tc.name+"/"+token, func(t *testing.T) {
				cmd, err := parse(tc.args)
				if err != nil || cmd.help || cmd.fieldsOnly || argValue(cmd.args, tc.key, "") != token {
					t.Fatalf("command=%+v error=%v", cmd, err)
				}
			})
		}
	}
}

// TestIssue682UnknownAndWrongLeafShortFlagsFailLocally proves an allowlisted
// short flag on a leaf without the long flag, and a short flag outside the
// allowlist, both exit 2 before any HTTP request.
func TestIssue682UnknownAndWrongLeafShortFlagsFailLocally(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{name: "allowlisted short flag wrong leaf", args: []string{"issue", "create", "Title", "--body", "b", "-m", "x", "--project", "proj"}},
		{name: "allowlisted short flag other wrong leaf", args: []string{"run", "stop", "wr1", "-l", "x"}},
		{name: "short flag outside allowlist", args: []string{"issue", "list", "-x", "--project", "proj"}},
		{name: "short flag with no long flag on leaf", args: []string{"issue", "create", "Title", "--body", "b", "-f", "--project", "proj"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			calls := 0
			deps, _, errOut := organizationDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return response(http.StatusInternalServerError, `{}`), nil
			}))
			if code := Run(context.Background(), tc.args, deps); code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if calls != 0 {
				t.Fatalf("HTTP requests issued=%d", calls)
			}
			if !strings.Contains(errOut.String(), "unknown option") {
				t.Fatalf("stderr=%q", errOut.String())
			}
		})
	}
}

// packagedSkillCommand pins one representative command to the packaged Skill
// that ships it. fragments are exact command-text substrings that must appear
// in the shipped SKILL.md; args is the tokenized command handed to the parser.
// The guard trips if either the document stops shipping the command text or
// the binary stops parsing it.
type packagedSkillCommand struct {
	skill     string
	doc       string
	fragments []string
	args      []string
}

// readPackagedSkill loads one shipped Skill document relative to the test
// package directory, so the contract never hardcodes a checkout path.
func readPackagedSkill(t *testing.T, skill string) string {
	t.Helper()
	path := filepath.Join("skill-data", skill, "SKILL.md")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read packaged Skill %s: %v", path, err)
	}
	return string(data)
}

// verifyPackagedSkillCommand asserts the command text is present in the
// shipped document and that the binary parses its tokenized form. It returns
// the first problem instead of failing the caller so the guard itself can be
// exercised with a synthetic drift case.
func verifyPackagedSkillCommand(cmd packagedSkillCommand) error {
	for _, fragment := range cmd.fragments {
		if !strings.Contains(cmd.doc, fragment) {
			return fmt.Errorf("packaged Skill %s does not ship command text %q", cmd.skill, fragment)
		}
	}
	if _, err := parse(cmd.args); err != nil {
		return fmt.Errorf("parse %q from packaged Skill %s: %w", strings.Join(cmd.args, " "), cmd.skill, err)
	}
	return nil
}

// TestIssue682PackagedSkillCommandsAreShippedAndParse pins the representative
// commands the mohist-create-issue and mohist-create-epic Skills ship: every
// command line must still be present in the packaged SKILL.md and the binary
// must accept it. Drift in either direction fails the test.
func TestIssue682PackagedSkillCommandsAreShippedAndParse(t *testing.T) {
	issueDoc := readPackagedSkill(t, "mohist-create-issue")
	epicDoc := readPackagedSkill(t, "mohist-create-epic")
	cases := []packagedSkillCommand{
		{
			skill:     "mohist-create-issue",
			doc:       issueDoc,
			fragments: []string{"mo issue template list"},
			args:      []string{"issue", "template", "list"},
		},
		{
			skill:     "mohist-create-issue",
			doc:       issueDoc,
			fragments: []string{"mo issue template view <id>"},
			args:      []string{"issue", "template", "view", "feature"},
		},
		{
			skill:     "mohist-create-issue",
			doc:       issueDoc,
			fragments: []string{"mo workflow list"},
			args:      []string{"workflow", "list"},
		},
		{
			skill:     "mohist-create-issue",
			doc:       issueDoc,
			fragments: []string{"mo label list"},
			args:      []string{"label", "list"},
		},
		{
			skill:     "mohist-create-issue",
			doc:       issueDoc,
			fragments: []string{"mo issue create <title> --body-file <produced-file>", "-l key=value"},
			args:      []string{"issue", "create", "Title", "--body-file", "body.md", "-l", "team=core"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{`mo epic create "<title>" --description-file ./epic.md --priority p2`},
			args:      []string{"epic", "create", "Epic", "--description-file", "epic.md", "--priority", "p2"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{"mo epic add <epic-id-or-number> <issue-id-or-number>"},
			args:      []string{"epic", "add", "7", "42"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{"mo epic remove <epic-id-or-number> <issue-id>"},
			args:      []string{"epic", "remove", "7", "42"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{"mo epic start <id>"},
			args:      []string{"epic", "start", "7"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{"mo epic pause <id>"},
			args:      []string{"epic", "pause", "7"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{"mo epic resume <id>"},
			args:      []string{"epic", "resume", "7"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{"mo epic done <id>"},
			args:      []string{"epic", "done", "7"},
		},
		{
			skill:     "mohist-create-epic",
			doc:       epicDoc,
			fragments: []string{"mo epic close <id>"},
			args:      []string{"epic", "close", "7"},
		},
	}
	for _, tc := range cases {
		t.Run(strings.Join(tc.args, " "), func(t *testing.T) {
			if err := verifyPackagedSkillCommand(tc); err != nil {
				t.Fatal(err)
			}
		})
	}
}

// TestIssue682PackagedSkillReadAndLifecycleCommandsParse parses the read and
// lifecycle commands the packaged Skills depend on, including the mo skill
// entry commands that only appear in prose rather than a single command line.
func TestIssue682PackagedSkillReadAndLifecycleCommandsParse(t *testing.T) {
	commands := [][]string{
		{"issue", "template", "list"},
		{"issue", "template", "view", "feature"},
		{"workflow", "list"},
		{"label", "list"},
		{"epic", "start", "7"},
		{"epic", "add", "7", "42"},
		{"epic", "pause", "7"},
		{"epic", "resume", "7"},
		{"epic", "done", "7"},
		{"epic", "close", "7"},
		{"epic", "remove", "7", "42"},
		{"skill", "list"},
		{"skill", "view", "mohist"},
	}
	for _, args := range commands {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			if _, err := parse(args); err != nil {
				t.Fatalf("parse(%v): %v", args, err)
			}
		})
	}
}

// TestIssue682PackagedSkillGuardDetectsDrift proves the contract guard fails
// when a Skill ships a command the binary rejects, and when the shipped
// document no longer contains the expected command text. This is the property
// that catches Skill/binary drift.
func TestIssue682PackagedSkillGuardDetectsDrift(t *testing.T) {
	shipped := "On confirm run `mo issue create Title -m drift` and stop."
	unparsable := packagedSkillCommand{
		skill:     "synthetic",
		doc:       shipped,
		fragments: []string{"mo issue create Title -m drift"},
		args:      []string{"issue", "create", "Title", "-m", "drift"},
	}
	if err := verifyPackagedSkillCommand(unparsable); err == nil {
		t.Fatal("expected a shipped but unparsable command to fail the guard")
	}

	missing := packagedSkillCommand{
		skill:     "synthetic",
		doc:       shipped,
		fragments: []string{"mo issue create Title --not-shipped"},
		args:      []string{"issue", "create", "Title", "--body", "b"},
	}
	if err := verifyPackagedSkillCommand(missing); err == nil {
		t.Fatal("expected a missing shipped command text to fail the guard")
	}
}
