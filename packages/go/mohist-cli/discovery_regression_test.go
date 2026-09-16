package mohistcli

import (
	"context"
	"strings"
	"testing"
)

func TestDiscoveryRejectsUnknownActionsLocally(t *testing.T) {
	for _, prefix := range [][]string{
		{"label"}, {"issue", "template"}, {"issue", "comment"}, {"issue", "prereq"}, {"issue", "watch"}, {"issue", "variable"}, {"issue", "github"}, {"event", "dead-letter"}, {"agent"}, {"agent", "job"}, {"agent", "subscription"}, {"session"}, {"session", "schedule"},
	} {
		for _, token := range []string{"--help", "-h", "--json"} {
			args := append(append([]string{}, prefix...), "bogus", token)
			t.Run(strings.Join(args, " "), func(t *testing.T) {
				probe := discoveryProbe{}
				out, errOut := &strings.Builder{}, &strings.Builder{}
				if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage || out.Len() != 0 || !strings.Contains(errOut.String(), "unknown") {
					t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
				}
				probe.assertUnused(t)
			})
		}
	}
}

func TestDiscoveryPreservesOptionValues(t *testing.T) {
	for _, token := range []string{"--help", "--json"} {
		for _, tc := range []struct {
			args []string
			key  string
		}{
			{[]string{"issue", "create", "Title", "--body", "body", "--risk", token}, "risk"},
			{[]string{"agent", "start", "--prompt", token}, "prompt"},
			{[]string{"session", "followup", "sess", "--text", token}, "text"},
			{[]string{"agent", "subscription", "create", "agent", "--name", "sub", "--match", "true", "--response-prompt", token}, "response-prompt"},
		} {
			t.Run(strings.Join(tc.args, " "), func(t *testing.T) {
				cmd, err := parse(tc.args)
				if err != nil || cmd.help || cmd.fieldsOnly || argValue(cmd.args, tc.key, "") != token {
					t.Fatalf("command=%+v error=%v", cmd, err)
				}
			})
		}
	}
}

func TestDiscoveryRetainsLeafHelpSyntax(t *testing.T) {
	for _, tc := range []struct {
		args []string
		want string
	}{
		{[]string{"event", "tail", "--help"}, "mo event tail"},
		{[]string{"event", "dead-letter", "list", "--help"}, "mo event dead-letter list"},
		{[]string{"activity", "list", "--help"}, "[--limit <1-200>]"},
	} {
		t.Run(strings.Join(tc.args, " "), func(t *testing.T) {
			cmd, err := parse(tc.args)
			if err != nil || !cmd.help || !strings.Contains(cmd.helpText, tc.want) || strings.Contains(cmd.helpText, "mo ops ") {
				t.Fatalf("command=%+v error=%v", cmd, err)
			}
		})
	}
}

func TestAuthTokenDiscoveryCannotAccessRemote(t *testing.T) {
	for _, args := range [][]string{{"auth", "token", "list", "--json"}, {"auth", "token", "list", "junk"}, {"auth", "token", "revoke", "--json"}} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage || out.Len() != 0 {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}
