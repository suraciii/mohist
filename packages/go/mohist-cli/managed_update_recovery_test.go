package mohistcli

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestManagedRecoveryRollsBackAmbiguousActivationAndClearsPending(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	transaction, originalUnit := seedManagedRecovery(t, fixture, "activating")
	fixture.files.put(fixture.unitPath, []byte("[Service]\nWorkingDirectory=/candidate\nExecStart=/candidate/Mohist.Server\n"), 0o600)

	if err := fixture.updater.recoverPendingManagedUpdate(context.Background(), fixture.runtimeRoot); err != nil {
		t.Fatal(err)
	}
	if fixture.files.text(fixture.unitPath) != originalUnit {
		t.Fatal("recovery did not restore the exact unit snapshot")
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("successful rollback recovery retained pending marker")
	}
	state := readManagedRecoveryState(t, fixture, transaction.ID)
	if state.Status != "rolled-back" || state.Failure != "" {
		t.Fatalf("recovered transaction = %#v", state)
	}
	if !fixture.commands.hasSystemctlMutation() {
		t.Fatal("ambiguous activation recovery did not restore the service")
	}
}

func TestManagedRecoveryCompletesVerifiedTransactionWithoutServiceMutation(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	transaction, _ := seedManagedRecovery(t, fixture, "verified")

	if err := fixture.updater.recoverPendingManagedUpdate(context.Background(), fixture.runtimeRoot); err != nil {
		t.Fatal(err)
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("verified recovery retained pending marker")
	}
	if fixture.commands.hasSystemctlMutation() {
		t.Fatalf("verified recovery mutated systemd: %#v", fixture.commands.calls)
	}
	if readManagedRecoveryState(t, fixture, transaction.ID).Status != "verified" {
		t.Fatal("verified recovery rewrote the committed transaction")
	}
}

func TestManagedRecoveryCompletesCandidatePointersFromActivatingState(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	transaction, originalUnit := seedManagedRecovery(t, fixture, "activating")
	seedManagedRecoveryCandidate(t, fixture, &transaction, originalUnit, true, true)
	fixture.control.serverCalls = 1
	if err := fixture.updater.completeVerifiedRecovery(
		context.Background(), fixture.runtimeRoot, &transaction, []string{"server"},
	); err != nil {
		t.Fatalf("candidate precondition = %v", err)
	}
	fixture.control.serverCalls = 1

	if err := fixture.updater.recoverPendingManagedUpdate(context.Background(), fixture.runtimeRoot); err != nil {
		t.Fatal(err)
	}
	if fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("candidate recovery retained pending marker")
	}
	if readManagedRecoveryState(t, fixture, transaction.ID).Status != "verified" {
		t.Fatal("candidate recovery did not complete the transaction")
	}
	if fixture.commands.hasSystemctlMutation() {
		t.Fatalf("candidate recovery mutated systemd: %#v", fixture.commands.calls)
	}
}

func TestManagedRecoveryRollsBackMixedPointerWindows(t *testing.T) {
	tests := []struct {
		name              string
		status            string
		activeCandidate   bool
		verifiedCandidate bool
	}{
		{name: "candidate verified pointer only", status: "activating", verifiedCandidate: true},
		{name: "candidate active pointer only after failed recovery", status: "recovery-failed", activeCandidate: true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newManagedUpdateFixture(t)
			transaction, originalUnit := seedManagedRecovery(t, fixture, test.status)
			seedManagedRecoveryCandidate(t, fixture, &transaction, originalUnit, test.activeCandidate, test.verifiedCandidate)

			if err := fixture.updater.recoverPendingManagedUpdate(context.Background(), fixture.runtimeRoot); err != nil {
				t.Fatal(err)
			}
			if fixture.files.text(fixture.unitPath) != originalUnit {
				t.Fatal("mixed-pointer recovery did not restore the exact unit")
			}
			active := fixture.pointer("active.json")
			verified := fixture.pointer("verified.json")
			if !managedPointerMatchesTargets(active, map[string]*managedRuntimeTarget{"server": fixture.commands.oldTarget}, []string{"server"}) ||
				!managedPointerMatchesTargets(verified, map[string]*managedRuntimeTarget{"server": fixture.commands.oldTarget}, []string{"server"}) {
				t.Fatal("mixed-pointer recovery did not restore both previous pointers")
			}
			if readManagedRecoveryState(t, fixture, transaction.ID).Status != "rolled-back" {
				t.Fatal("mixed-pointer recovery did not record rollback")
			}
		})
	}
}

func TestManagedRecoveryRestoresRunnerBeforeServer(t *testing.T) {
	fixture := newManagedFullUpdateFixture(t)
	transactionID := "11111111111111111111111111111111"
	transactionRoot := filepath.Join(fixture.runtimeRoot, "transactions", transactionID)
	active := fixture.pointer("active.json")
	targets := map[string]*managedRuntimeTarget{}
	services := map[string]*managedServiceSnapshot{}
	for _, component := range []string{"server", "runner"} {
		target, err := pointerTarget(active, component)
		if err != nil {
			t.Fatal(err)
		}
		unitName, _ := managedUnitName(component)
		unitPath := fixture.commands.unitPaths[unitName]
		snapshotPath := filepath.Join(transactionRoot, "snapshots", component+".service")
		fixture.files.put(snapshotPath, []byte(fixture.files.text(unitPath)), 0o600)
		targets[component] = target
		services[component] = &managedServiceSnapshot{
			Component: component, UnitPath: unitPath, UnitSnapshot: snapshotPath,
			UnitMode: 0o600, WasActive: true, WasEnabled: true, PreviousTarget: target,
		}
	}
	for _, name := range []string{"active.json", "verified.json"} {
		value, mode, _ := fixture.files.ReadFile(filepath.Join(fixture.runtimeRoot, name))
		fixture.files.put(filepath.Join(transactionRoot, "snapshots", name), value, mode)
	}
	transaction := managedTransaction{
		ID: transactionID, Status: "activating", Generation: 8,
		UnitDirectory: filepath.Dir(fixture.commands.unitPaths["mohist.service"]), Targets: targets, Services: services,
		CreatedAt: time.Unix(1, 0).UTC(), UpdatedAt: time.Unix(1, 0).UTC(),
	}
	writeManagedRecoveryTransaction(t, fixture, transaction)
	fixture.files.put(fixture.commands.unitPaths["mohist.service"], []byte("invalid candidate unit\n"), 0o600)

	if err := fixture.updater.recoverPendingManagedUpdate(context.Background(), fixture.runtimeRoot); err != nil {
		t.Fatal(err)
	}
	fixture.assertEventOrder(
		"write-unit:"+fixture.commands.unitPaths["mohist-runner.service"],
		"write-unit:"+fixture.commands.unitPaths["mohist.service"],
	)
}

func TestManagedRecoveryRejectsUntrustedSnapshotAndRetainsPending(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	transaction, _ := seedManagedRecovery(t, fixture, "activating")
	transaction.Services["server"].UnitSnapshot = "/tmp/untrusted.service"
	statePath := filepath.Join(fixture.runtimeRoot, "transactions", transaction.ID, "state.json")
	stateValue, _ := json.MarshalIndent(transaction, "", "  ")
	fixture.files.put(statePath, append(stateValue, '\n'), 0o600)

	err := fixture.updater.recoverPendingManagedUpdate(context.Background(), fixture.runtimeRoot)
	if err == nil || !strings.Contains(err.Error(), "snapshot is invalid") {
		t.Fatalf("error = %v", err)
	}
	if !fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("failed recovery cleared pending marker")
	}
	if fixture.commands.hasSystemctlMutation() {
		t.Fatalf("untrusted recovery mutated systemd: %#v", fixture.commands.calls)
	}
}

func TestManagedRecoveryRejectsUnitPathOutsideCapturedDirectory(t *testing.T) {
	fixture := newManagedUpdateFixture(t)
	transaction, _ := seedManagedRecovery(t, fixture, "activating")
	transaction.Services["server"].UnitPath = "/tmp/mohist.service"
	writeManagedRecoveryTransaction(t, fixture, transaction)

	err := fixture.updater.recoverPendingManagedUpdate(context.Background(), fixture.runtimeRoot)
	if err == nil || !strings.Contains(err.Error(), "unit path is invalid") {
		t.Fatalf("error = %v", err)
	}
	if !fixture.files.Exists(filepath.Join(fixture.runtimeRoot, "pending.json")) {
		t.Fatal("unit-path rejection cleared pending marker")
	}
	if fixture.commands.hasSystemctlMutation() {
		t.Fatalf("unit-path rejection mutated systemd: %#v", fixture.commands.calls)
	}
}

func seedManagedRecovery(t *testing.T, fixture *managedUpdateFixture, status string) (managedTransaction, string) {
	t.Helper()
	transactionID := "11111111111111111111111111111111"
	transactionRoot := filepath.Join(fixture.runtimeRoot, "transactions", transactionID)
	statePath := filepath.Join(transactionRoot, "state.json")
	snapshotPath := filepath.Join(transactionRoot, "snapshots", "server.service")
	originalUnit := fixture.files.text(fixture.unitPath)
	fixture.files.put(snapshotPath, []byte(originalUnit), 0o600)
	for _, name := range []string{"active.json", "verified.json"} {
		value, mode, err := fixture.files.ReadFile(filepath.Join(fixture.runtimeRoot, name))
		if err != nil {
			t.Fatal(err)
		}
		fixture.files.put(filepath.Join(transactionRoot, "snapshots", name), value, mode)
	}
	target := *fixture.commands.oldTarget
	transaction := managedTransaction{
		ID: transactionID, Status: status, Generation: 8, UnitDirectory: filepath.Dir(fixture.unitPath),
		Targets: map[string]*managedRuntimeTarget{"server": &target},
		Services: map[string]*managedServiceSnapshot{
			"server": {
				Component: "server", UnitPath: fixture.unitPath, UnitSnapshot: snapshotPath,
				UnitMode: 0o600, WasActive: true, WasEnabled: true, PreviousTarget: fixture.commands.oldTarget,
			},
		},
		CreatedAt: time.Unix(1, 0).UTC(), UpdatedAt: time.Unix(1, 0).UTC(),
	}
	stateValue, _ := json.MarshalIndent(transaction, "", "  ")
	fixture.files.put(statePath, append(stateValue, '\n'), 0o600)
	pendingValue, _ := json.MarshalIndent(managedPendingTransaction{TransactionID: transactionID, StatePath: statePath}, "", "  ")
	fixture.files.put(filepath.Join(fixture.runtimeRoot, "pending.json"), append(pendingValue, '\n'), 0o600)
	return transaction, originalUnit
}

func seedManagedRecoveryCandidate(
	t *testing.T,
	fixture *managedUpdateFixture,
	transaction *managedTransaction,
	originalUnit string,
	activeCandidate bool,
	verifiedCandidate bool,
) {
	t.Helper()
	root := filepath.Join(fixture.runtimeRoot, "releases", "mohist-server-"+managedTestCommit+"-g8", "server")
	candidate := *fixture.commands.oldTarget
	candidate.WorkingDirectory = root
	candidate.Entrypoint = filepath.Join(root, "Mohist.Server")
	candidate.Identity = managedRuntimeIdentity{
		Component: "server", Version: "0.0.0+" + managedTestCommit, SourceRevision: managedTestCommit,
		TreeHash: managedTestTree, ArtifactDigest: strings.Repeat("f", 64),
		ReleaseID: "mohist-server-" + managedTestCommit, Generation: 8, IsComplete: true,
	}
	transaction.Targets["server"] = &candidate
	writeManagedRecoveryTransaction(t, fixture, *transaction)
	identityValue, _ := json.MarshalIndent(candidate.Identity, "", "  ")
	fixture.files.put(filepath.Join(root, "runtime-identity.json"), append(identityValue, '\n'), 0o600)
	patched, err := patchManagedSystemdUnit([]byte(originalUnit), &candidate)
	if err != nil {
		t.Fatal(err)
	}
	fixture.files.put(fixture.unitPath, patched, 0o600)
	oldPointer := fixture.pointer("active.json")
	candidatePointer := updateManagedPointer(oldPointer, transaction.ID, transaction.Generation, transaction.Targets)
	if activeCandidate {
		value, _ := json.MarshalIndent(candidatePointer, "", "  ")
		fixture.files.put(filepath.Join(fixture.runtimeRoot, "active.json"), append(value, '\n'), 0o600)
	}
	if verifiedCandidate {
		value, _ := json.MarshalIndent(candidatePointer, "", "  ")
		fixture.files.put(filepath.Join(fixture.runtimeRoot, "verified.json"), append(value, '\n'), 0o600)
	}
}

func writeManagedRecoveryTransaction(t *testing.T, fixture *managedUpdateFixture, transaction managedTransaction) {
	t.Helper()
	statePath := filepath.Join(fixture.runtimeRoot, "transactions", transaction.ID, "state.json")
	stateValue, _ := json.MarshalIndent(transaction, "", "  ")
	fixture.files.put(statePath, append(stateValue, '\n'), 0o600)
	pendingValue, _ := json.MarshalIndent(managedPendingTransaction{TransactionID: transaction.ID, StatePath: statePath}, "", "  ")
	fixture.files.put(filepath.Join(fixture.runtimeRoot, "pending.json"), append(pendingValue, '\n'), 0o600)
}

func readManagedRecoveryState(t *testing.T, fixture *managedUpdateFixture, transactionID string) managedTransaction {
	t.Helper()
	path := filepath.Join(fixture.runtimeRoot, "transactions", transactionID, "state.json")
	value, mode, err := fixture.files.ReadFile(path)
	if err != nil || mode.Perm() != os.FileMode(0o600) {
		t.Fatalf("recovery state = mode %o, error %v", mode, err)
	}
	var transaction managedTransaction
	if json.Unmarshal(value, &transaction) != nil {
		t.Fatal("recovery state is invalid JSON")
	}
	return transaction
}
