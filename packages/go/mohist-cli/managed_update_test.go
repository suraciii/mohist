package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const (
	managedTestCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	managedTestTree   = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
	managedOldCommit  = "cccccccccccccccccccccccccccccccccccccccc"
)

func TestResolveManagedUnitDirectoryCanonicalizesRecoveryAuthority(t *testing.T) {
	wantRelative, err := filepath.Abs(filepath.Join("relative", "units"))
	if err != nil {
		t.Fatal(err)
	}
	tests := []struct {
		name      string
		requested string
		want      string
	}{
		{name: "default", want: "/home/test/.config/systemd/user"},
		{name: "absolute", requested: "/custom/../units", want: "/units"},
		{name: "relative", requested: "relative/units", want: wantRelative},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, err := resolveManagedUnitDirectory("/home/test", test.requested)
			if err != nil || got != test.want || !filepath.IsAbs(got) {
				t.Fatalf("unit directory = %q, %v; want %q", got, err, test.want)
			}
		})
	}
}

func TestManagedUpdateCommitsVerifiedServerRelease(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	if err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"server"}, RepoRoot: "/repo",
	}); err != nil {
		t.Fatal(err)
	}
	pointer := fixture.pointer("active.json")
	if pointerText(pointer, "status") != "verified" {
		t.Fatalf("active status = %q", pointerText(pointer, "status"))
	}
	target, err := pointerTarget(pointer, "server")
	if err != nil {
		t.Fatal(err)
	}
	if target.Identity.SourceRevision != managedTestCommit || target.Identity.Generation != 8 ||
		target.Identity.ReleaseID != "mohist-server-"+managedTestCommit {
		t.Fatalf("candidate identity = %#v", target.Identity)
	}
	if !strings.Contains(target.WorkingDirectory, managedTestCommit+"-g8/server") ||
		!fixture.files.Exists(target.Entrypoint) {
		t.Fatalf("candidate target = %#v", target)
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("pending marker remains after commit")
	}
	transaction := fixture.latestTransaction()
	if transaction.Status != "verified" {
		t.Fatalf("transaction status = %q", transaction.Status)
	}
	fixture.assertEventOrder("command:dotnet publish", "write-unit", "command:systemctl --user restart")
}

func TestManagedUpdateSystemdFakeValidatesRawWorkingDirectory(t *testing.T) {
	tests := []struct {
		name             string
		workingDirectory string
		wantExitCode     int
	}{
		{name: "quoted", workingDirectory: `"/runtime/server"`, wantExitCode: 1},
		{name: "relative", workingDirectory: "runtime/server", wantExitCode: 1},
		{name: "escaped percent", workingDirectory: "/runtime/release root/100%%/server"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newManagedUpdateFixture(t)
			unit := "[Service]\nWorkingDirectory=" + test.workingDirectory + "\nExecStart=/runtime/server/Mohist.Server\n"
			fixture.files.put(fixture.unitPath, []byte(unit), 0o600)

			result := fixture.commands.Run(context.Background(), managedCommand{
				Name: "systemctl",
				Args: []string{"--user", "restart", "mohist.service"},
			})

			if result.ExitCode != test.wantExitCode {
				t.Fatalf("restart exit code = %d, want %d", result.ExitCode, test.wantExitCode)
			}
		})
	}
}

func TestManagedUpdateActivatesIndentedServiceDirectivesWithoutCrossSectionPollution(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	unit := "[Unit]\n" +
		"WorkingDirectory=/unit/decoy\n" +
		"ExecStart=/unit/decoy\n" +
		"Environment=MOHIST_RUNTIME_IDENTITY_PATH=/unit/decoy.json\n\n" +
		"[Service]\n" +
		"  WorkingDirectory=/runtime/old/server\n" +
		"\tEnvironment=\"PATH=/usr/bin\"\n" +
		"\tEnvironment=\"MOHIST_RUNTIME_IDENTITY_PATH=/runtime/old/server/runtime-identity.json\"\n" +
		"  ExecStart=/runtime/old/server/Mohist.Server\n" +
		"Restart=on-failure\n\n" +
		"[Install]\n" +
		"WorkingDirectory=/install/decoy\n" +
		"ExecStart=/install/decoy\n" +
		"Environment=MOHIST_RUNTIME_IDENTITY_PATH=/install/decoy.json\n" +
		"WantedBy=default.target\n"
	fixture.files.put(fixture.unitPath, []byte(unit), 0o600)

	if err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"server"}, RepoRoot: "/repo",
	}); err != nil {
		t.Fatal(err)
	}
	if status := pointerText(fixture.pointer("active.json"), "status"); status != "verified" {
		t.Fatalf("active status = %q", status)
	}
	activated := fixture.files.text(fixture.unitPath)
	for _, preserved := range []string{
		"WorkingDirectory=/unit/decoy\n",
		"ExecStart=/unit/decoy\n",
		"Environment=MOHIST_RUNTIME_IDENTITY_PATH=/unit/decoy.json\n",
		"WorkingDirectory=/install/decoy\n",
		"ExecStart=/install/decoy\n",
		"Environment=MOHIST_RUNTIME_IDENTITY_PATH=/install/decoy.json\n",
	} {
		if !strings.Contains(activated, preserved) {
			t.Fatalf("activation changed non-Service directive %q:\n%s", preserved, activated)
		}
	}
}

func TestManagedUpdateCommitsVerifiedRunnerRelease(t *testing.T) {
	fixture := newManagedRunnerUpdateFixture(t)
	serverUnitPath := "/home/test/.config/systemd/user/mohist.service"
	originalServerUnit := fixture.files.text(serverUnitPath)

	if err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"runner"}, RepoRoot: "/repo",
	}); err != nil {
		t.Fatal(err)
	}

	pointer := fixture.pointer("verified.json")
	if pointerText(pointer, "status") != "verified" {
		t.Fatalf("verified status = %q", pointerText(pointer, "status"))
	}
	target, err := pointerTarget(pointer, "runner")
	if err != nil {
		t.Fatal(err)
	}
	if target.Identity.Component != "runner" || target.Identity.SourceRevision != managedTestCommit ||
		target.Identity.TreeHash != managedTestTree || target.Identity.ReleaseID != "mohist-runner-"+managedTestCommit ||
		target.Identity.Generation != 8 || target.Identity.RunnerID != "runner-1" ||
		target.Identity.BuildGitHash != managedTestCommit || !target.Identity.IsComplete || len(target.Identity.ArtifactDigest) != 64 {
		t.Fatalf("candidate Runner identity = %#v", target.Identity)
	}
	if target.NodeExecutable == nil || *target.NodeExecutable != "/usr/bin/node" ||
		target.DependencyRoot == nil || *target.DependencyRoot != target.WorkingDirectory ||
		!fixture.files.Exists(target.Entrypoint) {
		t.Fatalf("candidate Runner target = %#v", target)
	}
	unit := fixture.files.text(fixture.unitPath)
	for _, preserved := range []string{
		"# preserve Runner credential wiring\n",
		"Environment=\"RUNNER_ID=runner-1\"\n",
		"EnvironmentFile=-%h/.config/mohist/runner.env\n",
		"EnvironmentFile=-%h/.config/mohist/runner-managed.env\n",
		"LoadCredential=runner-auth:/run/credentials/runner-auth\n",
	} {
		if !strings.Contains(unit, preserved) {
			t.Fatalf("Runner unit lost %q: %s", preserved, unit)
		}
	}
	if len(fixture.control.runnerObservations) != 2 {
		t.Fatalf("Runner observations = %#v", fixture.control.runnerObservations)
	}
	oldObservation := fixture.control.runnerObservations[0]
	candidateObservation := fixture.control.runnerObservations[1]
	if oldObservation.Identity.SourceRevision != managedOldCommit || oldObservation.ConnectionGeneration != "old-connection" {
		t.Fatalf("initial Runner observation = %#v", oldObservation)
	}
	if differences := managedIdentityDifferences(candidateObservation.Identity, target.Identity); len(differences) != 0 ||
		candidateObservation.Status != "online" || candidateObservation.ConnectionState != "connected" ||
		candidateObservation.ConnectionGeneration != "candidate-connection" {
		t.Fatalf("candidate Runner observation = %#v, differences = %#v", candidateObservation, differences)
	}
	fixture.assertEventOrder(
		"control:observe-runner:old-connection",
		"command:systemctl --user restart mohist-runner.service",
		"control:observe-runner:candidate-connection",
	)
	if fixture.control.serverCalls != 0 || fixture.files.text(serverUnitPath) != originalServerUnit {
		t.Fatalf("server was touched: observations=%d unit=%q", fixture.control.serverCalls, fixture.files.text(serverUnitPath))
	}
	assertManagedUpdateDoesNotCallSystemdUnit(t, fixture.commands.calls, "mohist.service")
	if fixture.control.cancelCalls != 0 {
		t.Fatalf("successful Runner update cancelled its fence %d time(s)", fixture.control.cancelCalls)
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("pending marker remains after verified Runner commit")
	}
}

func TestManagedUpdateRunnerIdentityFailureCancelsBeforeRestoreAndVerifiesRollback(t *testing.T) {
	fixture := newManagedRunnerUpdateFixture(t)
	fixture.control.runnerCandidateMismatch = true
	serverUnitPath := "/home/test/.config/systemd/user/mohist.service"
	originalServerUnit := fixture.files.text(serverUnitPath)
	originalRunnerUnit := fixture.files.text(fixture.unitPath)
	originalActive := fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json"))
	oldEntrypoint := filepath.Join("/runtime/old/runner", "dist", "cli.js")
	oldEntrypointValue := fixture.files.text(oldEntrypoint)

	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"runner"}, RepoRoot: "/repo",
	})
	if err == nil || !strings.Contains(err.Error(), "runtime did not become ready") {
		t.Fatalf("error = %v", err)
	}
	transaction := fixture.latestTransaction()
	if transaction.Status != "rolled-back" {
		t.Fatalf("transaction status = %q", transaction.Status)
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("verified Runner rollback left pending marker")
	}
	if fixture.control.cancelCalls != 1 || fixture.control.cancelRunnerID != "runner-1" ||
		fixture.control.cancelInterruptID != transaction.InterruptID {
		t.Fatalf("Runner fence cancellation = calls %d runner %q interrupt %q, transaction %q",
			fixture.control.cancelCalls, fixture.control.cancelRunnerID, fixture.control.cancelInterruptID, transaction.InterruptID)
	}
	if fixture.files.text(fixture.unitPath) != originalRunnerUnit {
		t.Fatal("rollback did not restore the exact Runner unit")
	}
	if fixture.files.text(oldEntrypoint) != oldEntrypointValue || oldEntrypointValue == "" {
		t.Fatal("rollback did not preserve the old Runner release")
	}
	if fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json")) != originalActive {
		t.Fatal("rollback did not restore the exact active pointer")
	}
	if fixture.files.text(serverUnitPath) != originalServerUnit || fixture.control.serverCalls != 0 {
		t.Fatal("Runner rollback touched Server")
	}
	assertManagedUpdateDoesNotCallSystemdUnit(t, fixture.commands.calls, "mohist.service")
	if len(fixture.control.runnerObservations) != 3 {
		t.Fatalf("Runner observations = %#v", fixture.control.runnerObservations)
	}
	rollbackObservation := fixture.control.runnerObservations[2]
	if differences := managedIdentityDifferences(rollbackObservation.Identity, *fixture.control.runnerOld); len(differences) != 0 ||
		rollbackObservation.Status != "online" || rollbackObservation.ConnectionState != "connected" ||
		rollbackObservation.ConnectionGeneration != "rollback-connection" {
		t.Fatalf("rollback Runner observation = %#v, differences = %#v", rollbackObservation, differences)
	}
	fixture.assertEventOrder(
		"control:observe-runner:old-connection",
		"command:systemctl --user restart mohist-runner.service",
		"control:observe-runner:candidate-connection",
		"control:cancel-runner:",
		"write-unit:"+fixture.unitPath,
		"command:systemctl --user restart mohist-runner.service",
		"control:observe-runner:rollback-connection",
	)
}

func TestManagedUpdateComponentsRemainIsolated(t *testing.T) {
	t.Run("server only", func(t *testing.T) {
		fixture := newManagedUpdateFixture(t)
		runnerUnitPath := "/home/test/.config/systemd/user/mohist-runner.service"
		originalRunnerUnit := []byte("operator-owned Runner unit")
		fixture.files.put(runnerUnitPath, originalRunnerUnit, 0o640)

		if err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
			Components: []string{"server"}, RepoRoot: "/repo",
		}); err != nil {
			t.Fatal(err)
		}
		if fixture.control.runnerCalls != 0 || fixture.control.beginCalls != 0 || fixture.control.cancelCalls != 0 {
			t.Fatalf("server-only update used Runner control: observe=%d begin=%d cancel=%d",
				fixture.control.runnerCalls, fixture.control.beginCalls, fixture.control.cancelCalls)
		}
		if fixture.files.text(runnerUnitPath) != string(originalRunnerUnit) {
			t.Fatal("server-only update changed Runner unit")
		}
		assertManagedUpdateDoesNotCallSystemdUnit(t, fixture.commands.calls, "mohist-runner.service")
	})

	t.Run("runner only", func(t *testing.T) {
		fixture := newManagedRunnerUpdateFixture(t)
		serverUnitPath := "/home/test/.config/systemd/user/mohist.service"
		originalServerUnit := fixture.files.text(serverUnitPath)

		if err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
			Components: []string{"runner"}, RepoRoot: "/repo",
		}); err != nil {
			t.Fatal(err)
		}
		if fixture.control.serverCalls != 0 || fixture.files.text(serverUnitPath) != originalServerUnit {
			t.Fatal("runner-only update touched Server")
		}
		assertManagedUpdateDoesNotCallSystemdUnit(t, fixture.commands.calls, "mohist.service")
	})
}

func TestManagedUpdateFullTransactionDrainsRunnerAfterServerReadiness(t *testing.T) {
	fixture := newManagedFullUpdateFixture(t)

	if err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"server", "runner"}, RepoRoot: "/repo",
	}); err != nil {
		t.Fatal(err)
	}

	fixture.assertEventOrder(
		"write-unit:/home/test/.config/systemd/user/mohist.service",
		"command:systemctl --user restart mohist.service",
		"control:observe-server:candidate",
		"control:begin-runner:runner-1:",
		"write-unit:/home/test/.config/systemd/user/mohist-runner.service",
		"command:systemctl --user restart mohist-runner.service",
		"control:observe-runner:candidate-connection",
	)
	if fixture.latestTransaction().Status != "verified" {
		t.Fatalf("transaction = %#v", fixture.latestTransaction())
	}
}

func TestManagedUpdateCarriesVerifiedExecStartArgumentsIntoCandidate(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	arguments := []string{"--urls", "http://127.0.0.1:5080", "--log-level", "debug"}
	old := *fixture.commands.oldTarget
	old.Arguments = append([]string(nil), arguments...)
	fixture.commands.oldTarget = &old
	fixture.control.old = old.Identity
	for _, pointerName := range []string{"active.json", "verified.json"} {
		pointer := fixture.pointer(pointerName)
		pointer["server"], _ = json.Marshal(old)
		value, _ := json.MarshalIndent(pointer, "", "  ")
		fixture.files.put(filepath.Join(fixture.runtimeRoot, pointerName), append(value, '\n'), 0o600)
	}
	unit := "[Unit]\nDescription=Mohist Server\n\n[Service]\n" +
		"WorkingDirectory=/runtime/old/server\n" +
		"Environment=\"MOHIST_RUNTIME_IDENTITY_PATH=/runtime/old/server/runtime-identity.json\"\n" +
		"ExecStart=/runtime/old/server/Mohist.Server --urls http://127.0.0.1:5080 --log-level debug\n" +
		"Restart=on-failure\n\n[Install]\nWantedBy=default.target\n"
	fixture.files.put(fixture.unitPath, []byte(unit), 0o600)

	if err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"server"}, RepoRoot: "/repo",
	}); err != nil {
		t.Fatal(err)
	}

	target, err := pointerTarget(fixture.pointer("verified.json"), "server")
	if err != nil {
		t.Fatal(err)
	}
	if strings.Join(target.Arguments, "\x00") != strings.Join(arguments, "\x00") {
		t.Fatalf("candidate arguments = %#v, want %#v", target.Arguments, arguments)
	}
	for _, argument := range arguments {
		if !strings.Contains(fixture.files.text(fixture.unitPath), argument) {
			t.Fatalf("candidate unit lost argument %q: %s", argument, fixture.files.text(fixture.unitPath))
		}
	}
	transaction := fixture.latestTransaction()
	if strings.Join(transaction.Services["server"].PreviousTarget.Arguments, "\x00") != strings.Join(arguments, "\x00") {
		t.Fatalf("captured arguments = %#v", transaction.Services["server"].PreviousTarget.Arguments)
	}
}

func TestManagedUpdateInterruptStateWriteFailureDoesNotCancelUnstartedFence(t *testing.T) {
	fixture := newManagedRunnerUpdateFixture(t)
	fixture.files.failStateWriteNumber = 3

	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"runner"}, RepoRoot: "/repo",
	})
	if err == nil || !strings.Contains(err.Error(), "state write unavailable") {
		t.Fatalf("error = %v", err)
	}
	if fixture.control.beginCalls != 0 || fixture.control.cancelCalls != 0 {
		t.Fatalf("unstarted fence was used: begin=%d cancel=%d", fixture.control.beginCalls, fixture.control.cancelCalls)
	}
	if fixture.commands.hasSystemctlMutation() {
		t.Fatalf("systemd mutation after interrupt state failure: %#v", fixture.commands.calls)
	}
	transaction := fixture.latestTransaction()
	if transaction.Status != "rolled-back" || transaction.InterruptID != "" {
		t.Fatalf("persisted transaction = %#v", transaction)
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("verified rollback left a pending marker")
	}
}

func TestManagedUpdateBuildFailureDoesNotMutateService(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	fixture.commands.failPublish = true
	original := fixture.files.text(fixture.unitPath)
	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{Components: []string{"server"}, RepoRoot: "/repo"})
	if err == nil || !strings.Contains(err.Error(), "publish failed") {
		t.Fatalf("error = %v", err)
	}
	if got := fixture.files.text(fixture.unitPath); got != original {
		t.Fatalf("unit changed after build failure:\n%s", got)
	}
	if fixture.commands.hasSystemctlMutation() {
		t.Fatalf("systemd mutation after build failure: %#v", fixture.commands.calls)
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("build failure created pending marker")
	}
}

func TestManagedUpdateIdentityMismatchRollsBackAndVerifiesOldRuntime(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	fixture.control.candidateMismatch = true
	originalUnit := fixture.files.text(fixture.unitPath)
	originalActive := fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json"))
	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{Components: []string{"server"}, RepoRoot: "/repo"})
	if err == nil || !strings.Contains(err.Error(), "runtime did not become ready") {
		t.Fatalf("error = %v", err)
	}
	if fixture.files.text(fixture.unitPath) != originalUnit {
		t.Fatal("rollback did not restore exact unit")
	}
	if fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json")) != originalActive {
		t.Fatal("rollback did not restore exact active pointer")
	}
	if fixture.latestTransaction().Status != "rolled-back" {
		t.Fatalf("transaction = %#v", fixture.latestTransaction())
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("verified rollback left pending marker")
	}
	if fixture.control.serverCalls < 3 {
		t.Fatalf("server observations = %d; old runtime was not reverified", fixture.control.serverCalls)
	}
}

func TestManagedUpdateRollbackReadinessFailureRemainsFailClosed(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	fixture.control.candidateMismatch = true
	fixture.control.rollbackMismatch = true
	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{Components: []string{"server"}, RepoRoot: "/repo"})
	if err == nil || !strings.Contains(err.Error(), "recovery failed") {
		t.Fatalf("error = %v", err)
	}
	if fixture.latestTransaction().Status != "recovery-failed" {
		t.Fatalf("transaction = %#v", fixture.latestTransaction())
	}
	if !fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("recovery failure did not retain pending marker")
	}
}

func TestManagedUpdateBeginRunnerInterruptFailureRequiresConfirmedCancellation(t *testing.T) {
	tests := []struct {
		name              string
		cancelError       error
		wantStatus        string
		wantPending       bool
		wantRecoveryError bool
	}{
		{
			name:              "cancel fails",
			cancelError:       errors.New("cancel unavailable"),
			wantStatus:        "recovery-failed",
			wantPending:       true,
			wantRecoveryError: true,
		},
		{
			name:       "cancel succeeds",
			wantStatus: "rolled-back",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newManagedRunnerUpdateFixture(t)
			fixture.control.beginError = errors.New("runner interrupt request failed")
			fixture.control.cancelError = test.cancelError

			err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
				Components: []string{"runner"}, RepoRoot: "/repo",
			})
			if err == nil || !strings.Contains(err.Error(), "runner interrupt request failed") {
				t.Fatalf("error = %v", err)
			}
			if strings.Contains(err.Error(), "managed runtime recovery failed") != test.wantRecoveryError {
				t.Fatalf("recovery failure error = %v, want marker %t", err, test.wantRecoveryError)
			}
			if fixture.control.beginCalls != 1 || fixture.control.cancelCalls != 1 {
				t.Fatalf("interrupt calls begin=%d cancel=%d", fixture.control.beginCalls, fixture.control.cancelCalls)
			}
			transaction := fixture.latestTransaction()
			if fixture.control.cancelRunnerID != "runner-1" || fixture.control.cancelInterruptID != transaction.InterruptID {
				t.Fatalf("cancellation identity runner=%q interrupt=%q, transaction=%q", fixture.control.cancelRunnerID, fixture.control.cancelInterruptID, transaction.InterruptID)
			}
			if transaction.Status != test.wantStatus {
				t.Fatalf("transaction status = %q, want %q", transaction.Status, test.wantStatus)
			}
			pending := fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json"))
			if pending != test.wantPending {
				t.Fatalf("pending marker exists = %t, want %t", pending, test.wantPending)
			}
		})
	}
}

func TestManagedUpdateActiveRunnerWorkRequiresConfirmedFenceCancellation(t *testing.T) {
	tests := []struct {
		name              string
		cancelError       error
		wantStatus        string
		wantPending       bool
		wantRecoveryError bool
	}{
		{
			name:              "cancel fails",
			cancelError:       errors.New("cancel unavailable"),
			wantStatus:        "recovery-failed",
			wantPending:       true,
			wantRecoveryError: true,
		},
		{
			name:       "cancel succeeds",
			wantStatus: "rolled-back",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newManagedRunnerUpdateFixture(t)
			fixture.control.activeWorkCount = 3
			fixture.control.cancelError = test.cancelError
			originalUnit := fixture.files.text(fixture.unitPath)

			err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
				Components: []string{"runner"}, RepoRoot: "/repo",
			})
			if err == nil || !strings.Contains(err.Error(), "3 active work item(s)") {
				t.Fatalf("error = %v", err)
			}
			if strings.Contains(err.Error(), "managed runtime recovery failed") != test.wantRecoveryError {
				t.Fatalf("recovery failure error = %v, want marker %t", err, test.wantRecoveryError)
			}
			if fixture.commands.hasSystemctlMutation() {
				t.Fatalf("systemd activation ran while Runner work was active: %#v", fixture.commands.calls)
			}
			if fixture.files.text(fixture.unitPath) != originalUnit {
				t.Fatal("Runner unit changed while active work was draining")
			}
			if fixture.control.beginCalls != 1 || fixture.control.cancelCalls != 1 {
				t.Fatalf("interrupt calls begin=%d cancel=%d", fixture.control.beginCalls, fixture.control.cancelCalls)
			}
			transaction := fixture.latestTransaction()
			if fixture.control.cancelRunnerID != "runner-1" || fixture.control.cancelInterruptID != transaction.InterruptID {
				t.Fatalf("cancellation fence runner=%q interrupt=%q, transaction=%q", fixture.control.cancelRunnerID, fixture.control.cancelInterruptID, transaction.InterruptID)
			}
			if transaction.Status != test.wantStatus {
				t.Fatalf("transaction status = %q, want %q", transaction.Status, test.wantStatus)
			}
			pending := fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json"))
			if pending != test.wantPending {
				t.Fatalf("pending marker exists = %t, want %t", pending, test.wantPending)
			}
		})
	}
}

func TestManagedUpdatePromotionFailurePersistsRecoveryStateBeforeFailingClosed(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	fixture.files.renameError = errors.New("promotion unavailable")
	originalUnit := fixture.files.text(fixture.unitPath)
	originalActive := fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json"))

	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"server"}, RepoRoot: "/repo",
	})
	if err == nil || !strings.Contains(err.Error(), "release could not be installed") || !strings.Contains(err.Error(), "remains pending") {
		t.Fatalf("error = %v", err)
	}
	transaction := fixture.latestTransaction()
	if transaction.Status != "recovery-failed" {
		t.Fatalf("transaction status = %q, want recovery-failed", transaction.Status)
	}
	statePath := filepath.Join(fixture.runtimeRoot, "transactions", transaction.ID, "state.json")
	pendingPath := filepath.Join(fixture.runtimeRoot, "pending.json")
	for _, path := range []string{statePath, pendingPath} {
		file, ok := fixture.files.values[filepath.Clean(path)]
		if !ok || file.mode.Perm() != 0o600 || len(file.value) == 0 {
			t.Fatalf("persisted recovery file %s = mode %o, %q", path, file.mode, file.value)
		}
	}
	fixture.assertEventOrder("write-state:", "write-pending:", "rename:")
	if fixture.commands.hasSystemctlMutation() {
		t.Fatalf("systemd mutation after promotion failure: %#v", fixture.commands.calls)
	}
	if fixture.files.text(fixture.unitPath) != originalUnit {
		t.Fatal("service unit changed after promotion failure")
	}
	if fixture.files.text(filepath.Join(fixture.runtimeRoot, "active.json")) != originalActive {
		t.Fatal("active pointer changed after promotion failure")
	}
	if fixture.control.beginCalls != 0 || fixture.control.cancelCalls != 0 {
		t.Fatalf("interrupt calls after promotion failure begin=%d cancel=%d", fixture.control.beginCalls, fixture.control.cancelCalls)
	}
}

func TestManagedUpdateRejectsUnresolvedTransactionBeforeBuild(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	fixture.files.put(filepath.Join(fixture.runtimeRoot, "pending.json"), []byte("not trusted\n"), 0o600)
	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{Components: []string{"server"}, RepoRoot: "/repo"})
	if err == nil || !strings.Contains(err.Error(), "unresolved transaction") {
		t.Fatalf("error = %v", err)
	}
	for _, call := range fixture.commands.calls {
		if call.Name != "git" {
			t.Fatalf("side effect before unresolved gate: %#v", call)
		}
	}
}

func TestManagedUpdateRejectsConcurrentOperation(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	fixture.files.lockError = errors.New("another Mohist install or update is running")
	err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{Components: []string{"server"}, RepoRoot: "/repo"})
	if err == nil || !strings.Contains(err.Error(), "another Mohist") {
		t.Fatalf("error = %v", err)
	}
	for _, call := range fixture.commands.calls {
		if call.Name != "git" {
			t.Fatalf("side effect before lock: %#v", call)
		}
	}
}

func TestManagedUpdateRequiresExistingVerifiedRuntimeBeforeBuild(t *testing.T) {
	for _, marker := range []string{"active.json", "verified.json"} {
		t.Run(marker, func(t *testing.T) {
			fixture := newManagedUpdateFixture(t)
			_ = fixture.files.RemoveAll(filepath.Join(fixture.runtimeRoot, marker))

			err := fixture.updater.Update(context.Background(), ManagedUpdateRequest{
				Components: []string{"server"}, RepoRoot: "/repo",
			})
			if err == nil || !strings.Contains(err.Error(), "runtime is unavailable") {
				t.Fatalf("error = %v", err)
			}
			for _, call := range fixture.commands.calls {
				if call.Name != "git" {
					t.Fatalf("side effect before verified runtime gate: %#v", call)
				}
			}
			if fixture.control.serverCalls != 0 || fixture.control.beginCalls != 0 {
				t.Fatalf("control-plane calls before verified runtime gate: %#v", fixture.control)
			}
		})
	}
}

func TestManagedUpdateRequiresActiveAndVerifiedTargetsToAgreeBeforeCapture(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	verified := fixture.pointer("verified.json")
	target, err := pointerTarget(verified, "server")
	if err != nil {
		t.Fatal(err)
	}
	target.Entrypoint = "/runtime/unverified/server/Mohist.Server"
	verified["server"], _ = json.Marshal(target)
	value, _ := json.MarshalIndent(verified, "", "  ")
	fixture.files.put(filepath.Join(fixture.runtimeRoot, "verified.json"), append(value, '\n'), 0o600)

	err = fixture.updater.Update(context.Background(), ManagedUpdateRequest{
		Components: []string{"server"}, RepoRoot: "/repo",
	})
	if err == nil || !strings.Contains(err.Error(), "active and verified targets do not agree") {
		t.Fatalf("error = %v", err)
	}
	for _, call := range fixture.commands.calls {
		if call.Name != "git" {
			t.Fatalf("side effect before pointer agreement gate: %#v", call)
		}
	}
	for path := range fixture.files.values {
		if strings.Contains(path, "/transactions/") {
			t.Fatalf("transaction workspace created before pointer agreement: %s", path)
		}
	}
}

type managedUpdateFixture struct {
	t           testing.TB
	runtimeRoot string
	unitPath    string
	files       *managedUpdateFakeFiles
	commands    *managedUpdateFakeCommands
	control     *managedUpdateFakeControl
	updater     *managedUpdater
}

func newManagedUpdateFixture(t testing.TB) *managedUpdateFixture {
	t.Helper()
	files := &managedUpdateFakeFiles{values: map[string]managedUpdateFakeFile{}, events: &[]string{}}
	runtimeRoot := "/home/test/.local/share/mohist/runtime"
	unitPath := "/home/test/.config/systemd/user/mohist.service"
	old := managedRuntimeTarget{
		Component: "server", Entrypoint: "/runtime/old/server/Mohist.Server", WorkingDirectory: "/runtime/old/server",
		Arguments: []string{}, RuntimeIdentifier: "linux-x64", LaunchMode: 0,
		IsAbsoluteTarget: true, UsesCanonicalEntrypoint: true,
		Identity: managedRuntimeIdentity{
			Component: "server", Version: "0.0.0+" + managedOldCommit, SourceRevision: managedOldCommit,
			TreeHash: strings.Repeat("d", 40), ArtifactDigest: strings.Repeat("e", 64),
			ReleaseID: "mohist-server-" + managedOldCommit, Generation: 7, IsComplete: true,
		},
	}
	pointer := managedPointer{}
	setPointer := func(key string, value any) { pointer[key], _ = json.Marshal(value) }
	setPointer("status", "verified")
	setPointer("generation", 7)
	setPointer("transactionId", "old")
	setPointer("server", old)
	setPointer("cli", nil)
	setPointer("runner", nil)
	pointerValue, _ := json.MarshalIndent(pointer, "", "  ")
	files.put(filepath.Join(runtimeRoot, "active.json"), append(pointerValue, '\n'), 0o600)
	files.put(filepath.Join(runtimeRoot, "verified.json"), append(pointerValue, '\n'), 0o600)
	files.put("/repo/Mohist.sln", []byte("solution"), 0o644)
	unit := "[Unit]\nDescription=Mohist Server\n\n[Service]\nWorkingDirectory=/runtime/old/server\nEnvironment=\"PATH=/usr/bin\"\nEnvironment=\"MOHIST_RUNTIME_IDENTITY_PATH=/runtime/old/server/runtime-identity.json\"\nExecStart=/runtime/old/server/Mohist.Server\nRestart=on-failure\n\n[Install]\nWantedBy=default.target\n"
	files.put(unitPath, []byte(unit), 0o600)
	commands := &managedUpdateFakeCommands{
		files: files, oldTarget: &old, unitPath: unitPath,
		unitPaths: map[string]string{"mohist.service": unitPath},
	}
	control := &managedUpdateFakeControl{files: files, old: old.Identity}
	now := time.Date(2030, 1, 2, 3, 4, 5, 0, time.UTC)
	env := managedUpdateEnvironment{
		files: files, commands: commands, control: control,
		now:     func() time.Time { return now },
		wait:    func(context.Context, time.Duration) error { now = now.Add(time.Minute); return nil },
		newID:   func() string { return "11111111111111111111111111111111" },
		homeDir: func() (string, error) { return "/home/test", nil },
		stdout:  io.Discard, stderr: io.Discard,
	}
	return &managedUpdateFixture{t, runtimeRoot, unitPath, files, commands, control, &managedUpdater{env: env}}
}

func newManagedRunnerUpdateFixture(t testing.TB) *managedUpdateFixture {
	t.Helper()
	fixture := newManagedUpdateFixture(t)
	addManagedTestRunner(fixture, false)
	return fixture
}

func newManagedFullUpdateFixture(t testing.TB) *managedUpdateFixture {
	t.Helper()
	fixture := newManagedUpdateFixture(t)
	addManagedTestRunner(fixture, true)
	return fixture
}

func addManagedTestRunner(fixture *managedUpdateFixture, retainServer bool) {
	runnerRoot := "/runtime/old/runner"
	node := "/usr/bin/node"
	old := managedRuntimeTarget{
		Component: "runner", Entrypoint: filepath.Join(runnerRoot, "dist", "cli.js"), WorkingDirectory: runnerRoot,
		Arguments: []string{}, RuntimeIdentifier: managedRuntimeIdentifier(), NodeExecutable: &node,
		DependencyRoot: &runnerRoot, LaunchMode: 1, IsAbsoluteTarget: true, UsesCanonicalEntrypoint: true,
		Identity: managedRuntimeIdentity{
			Component: "runner", Version: "0.0.0+" + managedOldCommit, SourceRevision: managedOldCommit,
			TreeHash: strings.Repeat("d", 40), ArtifactDigest: strings.Repeat("e", 64),
			ReleaseID: "mohist-runner-" + managedOldCommit, Generation: 7, RunnerID: "runner-1",
			BuildGitHash: managedOldCommit, IsComplete: true,
		},
	}
	for _, pointerName := range []string{"active.json", "verified.json"} {
		pointer := fixture.pointer(pointerName)
		pointer["runner"], _ = json.Marshal(old)
		if !retainServer {
			pointer["server"], _ = json.Marshal(nil)
		}
		pointerValue, _ := json.MarshalIndent(pointer, "", "  ")
		fixture.files.put(filepath.Join(fixture.runtimeRoot, pointerName), append(pointerValue, '\n'), 0o600)
	}

	fixture.unitPath = "/home/test/.config/systemd/user/mohist-runner.service"
	unit := "[Unit]\nDescription=Mohist Runner\n\n[Service]\n" +
		"# preserve Runner credential wiring\n" +
		"WorkingDirectory=/runtime/old/runner\n" +
		"Environment=\"RUNNER_ID=runner-1\"\n" +
		"Environment=\"MOHIST_RUNTIME_IDENTITY_PATH=/runtime/old/runner/runtime-identity.json\"\n" +
		"EnvironmentFile=-%h/.config/mohist/runner.env\n" +
		"EnvironmentFile=-%h/.config/mohist/runner-managed.env\n" +
		"LoadCredential=runner-auth:/run/credentials/runner-auth\n" +
		"ExecStart=/usr/bin/node /runtime/old/runner/dist/cli.js\nRestart=always\n\n" +
		"[Install]\nWantedBy=default.target\n"
	fixture.files.put(fixture.unitPath, []byte(unit), 0o600)
	fixture.files.put(old.Entrypoint, []byte("old-runner-payload"), 0o755)
	oldIdentity, _ := json.MarshalIndent(old.Identity, "", "  ")
	fixture.files.put(filepath.Join(runnerRoot, "runtime-identity.json"), append(oldIdentity, '\n'), 0o600)
	fixture.commands.unitPath = fixture.unitPath
	fixture.commands.unitPaths["mohist-runner.service"] = fixture.unitPath
	fixture.commands.oldTarget = &old
	if !retainServer {
		fixture.control.old = old.Identity
	}
	fixture.control.runnerOld = &old.Identity
	fixture.control.runnerUnitPath = fixture.unitPath
}

func (fixture *managedUpdateFixture) pointer(name string) managedPointer {
	fixture.t.Helper()
	var value managedPointer
	if err := json.Unmarshal([]byte(fixture.files.text(filepath.Join(fixture.runtimeRoot, name))), &value); err != nil {
		fixture.t.Fatal(err)
	}
	return value
}

func (fixture *managedUpdateFixture) latestTransaction() managedTransaction {
	fixture.t.Helper()
	path := filepath.Join(fixture.runtimeRoot, "transactions", "11111111111111111111111111111111", "state.json")
	var value managedTransaction
	if err := json.Unmarshal([]byte(fixture.files.text(path)), &value); err != nil {
		fixture.t.Fatal(err)
	}
	return value
}

func (fixture *managedUpdateFixture) assertEventOrder(wants ...string) {
	fixture.t.Helper()
	index := -1
	for _, want := range wants {
		found := -1
		for candidate := index + 1; candidate < len(*fixture.files.events); candidate++ {
			if strings.HasPrefix((*fixture.files.events)[candidate], want) {
				found = candidate
				break
			}
		}
		if found < 0 {
			fixture.t.Fatalf("event %q missing after %d: %#v", want, index, *fixture.files.events)
		}
		index = found
	}
}

func assertManagedUpdateDoesNotCallSystemdUnit(t *testing.T, calls []managedCommand, unitName string) {
	t.Helper()
	for _, call := range calls {
		if call.Name != "systemctl" {
			continue
		}
		for _, argument := range call.Args {
			if argument == unitName {
				t.Fatalf("unexpected systemd call for %s: %#v", unitName, call)
			}
		}
	}
}

type managedUpdateFakeFile struct {
	value []byte
	mode  os.FileMode
}

type managedUpdateFakeFiles struct {
	values               map[string]managedUpdateFakeFile
	events               *[]string
	lockError            error
	renameError          error
	stateWrites          int
	failStateWriteNumber int
}

func (files *managedUpdateFakeFiles) put(path string, value []byte, mode os.FileMode) {
	files.values[filepath.Clean(path)] = managedUpdateFakeFile{append([]byte(nil), value...), mode}
}
func (files *managedUpdateFakeFiles) text(path string) string {
	return string(files.values[filepath.Clean(path)].value)
}
func (files *managedUpdateFakeFiles) Exists(path string) bool {
	_, ok := files.values[filepath.Clean(path)]
	return ok
}
func (files *managedUpdateFakeFiles) ReadFile(path string) ([]byte, os.FileMode, error) {
	value, ok := files.values[filepath.Clean(path)]
	if !ok {
		return nil, 0, os.ErrNotExist
	}
	return append([]byte(nil), value.value...), value.mode, nil
}
func (files *managedUpdateFakeFiles) WriteFileAtomic(path string, value []byte, mode os.FileMode) error {
	path = filepath.Clean(path)
	if strings.HasSuffix(path, ".service") && !strings.Contains(path, "/snapshots/") {
		*files.events = append(*files.events, "write-unit:"+path)
	}
	if filepath.Base(path) == "state.json" {
		files.stateWrites++
		*files.events = append(*files.events, "write-state:"+path)
		if files.failStateWriteNumber == files.stateWrites {
			return errors.New("state write unavailable")
		}
	}
	if filepath.Base(path) == "pending.json" {
		*files.events = append(*files.events, "write-pending:"+path)
	}
	files.put(path, value, mode)
	return nil
}
func (files *managedUpdateFakeFiles) MkdirAll(string, os.FileMode) error { return nil }
func (files *managedUpdateFakeFiles) RemoveAll(path string) error {
	path = filepath.Clean(path)
	for candidate := range files.values {
		if candidate == path || strings.HasPrefix(candidate, path+string(filepath.Separator)) {
			delete(files.values, candidate)
		}
	}
	return nil
}
func (files *managedUpdateFakeFiles) Rename(from, to string) error {
	from, to = filepath.Clean(from), filepath.Clean(to)
	*files.events = append(*files.events, "rename:"+from+":"+to)
	if files.renameError != nil {
		return files.renameError
	}
	found := false
	for candidate, value := range files.values {
		if candidate == from || strings.HasPrefix(candidate, from+string(filepath.Separator)) {
			relative := strings.TrimPrefix(candidate, from)
			files.values[to+relative] = value
			delete(files.values, candidate)
			found = true
		}
	}
	if !found {
		return os.ErrNotExist
	}
	return nil
}
func (files *managedUpdateFakeFiles) WalkFiles(root string) ([]string, error) {
	root = filepath.Clean(root)
	paths := []string{}
	for candidate := range files.values {
		if strings.HasPrefix(candidate, root+string(filepath.Separator)) {
			relative, _ := filepath.Rel(root, candidate)
			paths = append(paths, relative)
		}
	}
	return paths, nil
}
func (files *managedUpdateFakeFiles) OpenLock(string) (io.Closer, error) {
	if files.lockError != nil {
		return nil, files.lockError
	}
	return io.NopCloser(strings.NewReader("")), nil
}

type managedUpdateFakeCommands struct {
	files       *managedUpdateFakeFiles
	oldTarget   *managedRuntimeTarget
	unitPath    string
	unitPaths   map[string]string
	calls       []managedCommand
	failPublish bool
}

func (commands *managedUpdateFakeCommands) Run(_ context.Context, command managedCommand) managedCommandResult {
	commands.calls = append(commands.calls, command)
	*commands.files.events = append(*commands.files.events, "command:"+command.Name+" "+strings.Join(command.Args, " "))
	if command.Name == "git" {
		switch strings.Join(command.Args, " ") {
		case "rev-parse --show-toplevel":
			return managedCommandResult{Stdout: "/repo\n"}
		case "rev-parse --verify HEAD":
			return managedCommandResult{Stdout: managedTestCommit + "\n"}
		case "rev-parse --verify HEAD^{tree}":
			return managedCommandResult{Stdout: managedTestTree + "\n"}
		case "status --porcelain --untracked-files=all":
			return managedCommandResult{}
		default:
			return managedCommandResult{}
		}
	}
	if command.Name == "dotnet" && len(command.Args) > 0 && command.Args[0] == "publish" {
		if commands.failPublish {
			return managedCommandResult{ExitCode: 9, Stderr: "secret must stay hidden"}
		}
		output := command.Args[len(command.Args)-1]
		commands.files.put(filepath.Join(output, "Mohist.Server"), []byte("server-payload"), 0o755)
		return managedCommandResult{}
	}
	if command.Name == "npm" && len(command.Args) >= 2 && command.Args[0] == "run" && command.Args[1] == "build" {
		commands.files.put(filepath.Join(command.Dir, "packages", "runner", "dist", "cli.js"), []byte("runner-payload"), 0o755)
		commands.files.put(filepath.Join(command.Dir, "packages", "runner", "package.json"), []byte(`{"name":"runner"}`), 0o600)
		commands.files.put(filepath.Join(command.Dir, "node_modules", "hoisted.js"), []byte("hoisted"), 0o600)
		commands.files.put(filepath.Join(command.Dir, "packages", "runner", "node_modules", "local.js"), []byte("local"), 0o600)
		return managedCommandResult{}
	}
	if command.Name == "cp" && len(command.Args) == 3 {
		commands.copy(command.Args[1], command.Args[2])
		return managedCommandResult{}
	}
	if command.Name == "sh" {
		return managedCommandResult{Stdout: "/usr/bin/node\n"}
	}
	if command.Name == "systemctl" {
		operation := command.Args[1]
		switch operation {
		case "is-active":
			return managedCommandResult{Stdout: "active\n"}
		case "is-enabled":
			return managedCommandResult{Stdout: "enabled\n"}
		case "show":
			property := strings.TrimPrefix(command.Args[3], "--property=")
			unitPath := commands.unitPaths[command.Args[2]]
			if unitPath == "" {
				unitPath = commands.unitPath
			}
			if property == "FragmentPath" {
				return managedCommandResult{Stdout: unitPath + "\n"}
			}
			unit := commands.files.text(unitPath)
			return managedCommandResult{Stdout: managedTestSystemdProperty(unit, property)}
		case "restart":
			if len(command.Args) != 3 {
				return managedCommandResult{ExitCode: 2}
			}
			unitPath := commands.unitPaths[command.Args[2]]
			unit := commands.files.text(unitPath)
			workingDirectories := managedTestSystemdDirectiveValues(unit, "WorkingDirectory")
			if len(workingDirectories) != 1 {
				return managedCommandResult{ExitCode: 1}
			}
			if _, err := parseManagedSystemdWorkingDirectory(workingDirectories[0]); err != nil {
				return managedCommandResult{ExitCode: 1}
			}
			return managedCommandResult{}
		default:
			return managedCommandResult{}
		}
	}
	return managedCommandResult{}
}

func (commands *managedUpdateFakeCommands) copy(from, to string) {
	from = filepath.Clean(from)
	prefix := from + string(filepath.Separator)
	type fileCopy struct {
		path string
		file managedUpdateFakeFile
	}
	copies := []fileCopy{}
	for path, file := range commands.files.values {
		if path == from || strings.HasPrefix(path, prefix) {
			copies = append(copies, fileCopy{path: path, file: file})
		}
	}
	for _, copy := range copies {
		relative := strings.TrimPrefix(copy.path, prefix)
		destination := to
		if copy.path != from {
			destination = filepath.Join(to, relative)
		}
		commands.files.put(destination, copy.file.value, copy.file.mode)
	}
}

func (commands *managedUpdateFakeCommands) hasSystemctlMutation() bool {
	for _, call := range commands.calls {
		if call.Name == "systemctl" && len(call.Args) > 1 && call.Args[1] != "is-active" && call.Args[1] != "is-enabled" && call.Args[1] != "show" {
			return true
		}
	}
	return false
}

func managedTestSystemdProperty(unit, property string) string {
	values := managedTestSystemdDirectiveValues(unit, property)
	switch property {
	case "WorkingDirectory":
		if len(values) == 1 {
			if decoded, err := parseManagedSystemdWorkingDirectory(values[0]); err == nil {
				values[0] = decoded
			}
		}
	case "ExecStart", "Environment":
		for index := range values {
			values[index] = strings.ReplaceAll(values[index], "%%", "%")
		}
	}
	return strings.Join(values, " ") + "\n"
}

func managedTestSystemdDirectiveValues(unit, property string) []string {
	values := []string{}
	inService := false
	serviceSections := 0
	for _, line := range splitManagedUnitLines([]byte(unit)) {
		body := string(line.body)
		trimmed := strings.TrimSpace(body)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			inService = trimmed == "[Service]"
			if inService {
				serviceSections++
			}
			continue
		}
		if !inService {
			continue
		}
		key, value, _, ok := managedUnitDirective(body)
		if ok && key == property {
			values = append(values, value)
		}
	}
	if serviceSections != 1 {
		return nil
	}
	return values
}

type managedUpdateFakeControl struct {
	files                   *managedUpdateFakeFiles
	old                     managedRuntimeIdentity
	runnerOld               *managedRuntimeIdentity
	runnerUnitPath          string
	serverCalls             int
	runnerCalls             int
	candidateMismatch       bool
	rollbackMismatch        bool
	runnerCandidateMismatch bool
	runnerSawCandidate      bool
	runnerObservations      []managedRuntimeObservation
	runnerObserveIDs        []string
	beginError              error
	cancelError             error
	activeWorkCount         int
	beginCalls              int
	cancelCalls             int
	cancelRunnerID          string
	cancelInterruptID       string
}

func (control *managedUpdateFakeControl) ObserveServer(context.Context) (managedRuntimeObservation, error) {
	control.serverCalls++
	if control.serverCalls == 1 {
		return control.recordServerObservation("old", managedRuntimeObservation{Identity: control.old, Status: "ok"}), nil
	}
	if control.candidateMismatch && control.serverCalls == 2 {
		return control.recordServerObservation("candidate-mismatch", managedRuntimeObservation{Identity: control.old, Status: "ok"}), nil
	}
	if control.rollbackMismatch && control.serverCalls >= 3 {
		return control.recordServerObservation("rollback-mismatch", managedRuntimeObservation{Identity: managedRuntimeIdentity{Component: "server"}, Status: "ok"}), nil
	}
	if control.candidateMismatch {
		return control.recordServerObservation("rollback", managedRuntimeObservation{Identity: control.old, Status: "ok"}), nil
	}
	for path, file := range control.files.values {
		if strings.Contains(path, managedTestCommit+"-g8/server/runtime-identity.json") {
			var identity managedRuntimeIdentity
			_ = json.Unmarshal(file.value, &identity)
			return control.recordServerObservation("candidate", managedRuntimeObservation{Identity: identity, Status: "ok"}), nil
		}
	}
	return managedRuntimeObservation{}, errors.New("candidate identity missing")
}

func (control *managedUpdateFakeControl) recordServerObservation(stage string, observation managedRuntimeObservation) managedRuntimeObservation {
	*control.files.events = append(*control.files.events, "control:observe-server:"+stage+":"+observation.Identity.SourceRevision)
	return observation
}
func (control *managedUpdateFakeControl) ObserveRunner(_ context.Context, runnerID string) (managedRuntimeObservation, error) {
	control.runnerCalls++
	control.runnerObserveIDs = append(control.runnerObserveIDs, runnerID)
	if control.runnerOld == nil {
		return managedRuntimeObservation{}, errors.New("unexpected Runner observation")
	}
	if runnerID != control.runnerOld.RunnerID {
		return managedRuntimeObservation{}, errors.New("unexpected Runner identity lookup")
	}
	unit := control.files.text(control.runnerUnitPath)
	isCandidate := strings.Contains(unit, managedTestCommit+"-g8/runner")
	identity := *control.runnerOld
	connectionGeneration := "old-connection"
	if isCandidate {
		control.runnerSawCandidate = true
		connectionGeneration = "candidate-connection"
		for path, file := range control.files.values {
			if strings.Contains(path, managedTestCommit+"-g8/runner/runtime-identity.json") {
				if err := json.Unmarshal(file.value, &identity); err != nil {
					return managedRuntimeObservation{}, errors.New("candidate Runner identity is invalid")
				}
				break
			}
		}
		if control.runnerCandidateMismatch {
			identity = *control.runnerOld
		}
	} else if control.runnerSawCandidate {
		connectionGeneration = "rollback-connection"
	}
	observation := managedRuntimeObservation{
		Identity: identity, Status: "online", ConnectionState: "connected", ConnectionGeneration: connectionGeneration,
	}
	control.runnerObservations = append(control.runnerObservations, observation)
	*control.files.events = append(*control.files.events, "control:observe-runner:"+connectionGeneration+":"+identity.SourceRevision)
	return observation, nil
}
func (control *managedUpdateFakeControl) BeginRunnerInterrupt(_ context.Context, runnerID, interruptID string) (managedRunnerInterrupt, error) {
	control.beginCalls++
	*control.files.events = append(*control.files.events, "control:begin-runner:"+runnerID+":"+interruptID)
	if control.runnerOld == nil {
		return managedRunnerInterrupt{}, errors.New("unexpected Runner interrupt")
	}
	if control.beginError != nil {
		return managedRunnerInterrupt{}, control.beginError
	}
	return managedRunnerInterrupt{
		RunnerID: runnerID, InterruptID: interruptID, Status: "draining", ActiveWorkCount: control.activeWorkCount,
	}, nil
}
func (control *managedUpdateFakeControl) CancelRunnerInterrupt(_ context.Context, runnerID, interruptID string) error {
	control.cancelCalls++
	control.cancelRunnerID = runnerID
	control.cancelInterruptID = interruptID
	*control.files.events = append(*control.files.events, "control:cancel-runner:"+runnerID+":"+interruptID)
	if control.runnerOld == nil {
		return errors.New("unexpected Runner interrupt cancellation")
	}
	return control.cancelError
}
