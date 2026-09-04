package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"strings"
)

type managedPendingTransaction struct {
	TransactionID string `json:"transactionId"`
	StatePath     string `json:"statePath"`
}

func (updater *managedUpdater) recoverPendingManagedUpdate(ctx context.Context, runtimeRoot string) error {
	env := updater.env
	pendingPath := filepath.Join(runtimeRoot, "pending.json")
	pendingValue, pendingMode, err := env.files.ReadFile(pendingPath)
	if err != nil || pendingMode.Perm() != 0o600 {
		return errors.New("pending marker is unavailable")
	}
	var pending managedPendingTransaction
	if json.Unmarshal(pendingValue, &pending) != nil {
		return errors.New("pending marker is invalid")
	}
	transactionRoot, err := managedTransactionRoot(runtimeRoot, pending.TransactionID)
	if err != nil {
		return errors.New("pending transaction identity is invalid")
	}
	statePath := filepath.Join(transactionRoot, "state.json")
	if !sameManagedPath(statePath, pending.StatePath) {
		return errors.New("pending transaction state path is invalid")
	}
	stateValue, stateMode, err := env.files.ReadFile(statePath)
	if err != nil || stateMode.Perm() != 0o600 {
		return errors.New("pending transaction state is unavailable")
	}
	var transaction managedTransaction
	if json.Unmarshal(stateValue, &transaction) != nil || transaction.ID != pending.TransactionID {
		return errors.New("pending transaction state is invalid")
	}
	components, err := validateManagedRecoveryTransaction(env.files, transactionRoot, &transaction)
	if err != nil {
		return err
	}

	if !containsManagedState([]string{"prepared", "activating", "verified", "rolled-back", "recovery-failed"}, transaction.Status) {
		return errors.New("pending transaction status is not recoverable")
	}

	active, activeMode, err := readManagedRecoveryPointer(env.files, transactionRoot, "active.json")
	if err != nil {
		return err
	}
	verified, verifiedMode, err := readManagedRecoveryPointer(env.files, transactionRoot, "verified.json")
	if err != nil {
		return err
	}
	for _, component := range components {
		activeTarget, activeErr := pointerTarget(active, component)
		verifiedTarget, verifiedErr := pointerTarget(verified, component)
		previous := transaction.Services[component].PreviousTarget
		if activeErr != nil || verifiedErr != nil || !reflect.DeepEqual(activeTarget, verifiedTarget) || !reflect.DeepEqual(activeTarget, previous) {
			return fmt.Errorf("pending %s recovery target is inconsistent", component)
		}
	}

	currentActive, _, _, activeErr := readManagedPointer(env.files, filepath.Join(runtimeRoot, "active.json"))
	currentVerified, _, _, verifiedErr := readManagedPointer(env.files, filepath.Join(runtimeRoot, "verified.json"))
	if activeErr == nil &&
		verifiedErr == nil &&
		managedPointerMatchesTargets(currentActive, transaction.Targets, components) &&
		managedPointerMatchesTargets(currentVerified, transaction.Targets, components) {
		if err := updater.completeVerifiedRecovery(ctx, runtimeRoot, &transaction, components); err == nil {
			transaction.Status = "verified"
			transaction.Failure = ""
			transaction.UpdatedAt = env.now()
			if err := writeManagedTransaction(env.files, statePath, &transaction); err != nil {
				return errors.New("verified recovery state could not be persisted")
			}
			if err := env.files.RemoveAll(pendingPath); err != nil {
				return errors.New("verified recovery marker could not be cleared")
			}
			return nil
		}
	}

	recoveryErrors := []string{}
	if transaction.InterruptID != "" {
		if runner := transaction.Targets["runner"]; runner != nil {
			if err := env.control.CancelRunnerInterrupt(context.Background(), runner.Identity.RunnerID, transaction.InterruptID); err != nil {
				recoveryErrors = append(recoveryErrors, "Runner interrupt")
			}
		}
	}
	for index := len(components) - 1; index >= 0; index-- {
		component := components[index]
		if err := restoreManagedService(context.Background(), env, transaction.Services[component]); err != nil {
			recoveryErrors = append(recoveryErrors, component+" service")
		}
	}
	if err := writeManagedPointer(env.files, filepath.Join(runtimeRoot, "active.json"), active, activeMode); err != nil {
		recoveryErrors = append(recoveryErrors, "active pointer")
	}
	if err := writeManagedPointer(env.files, filepath.Join(runtimeRoot, "verified.json"), verified, verifiedMode); err != nil {
		recoveryErrors = append(recoveryErrors, "verified pointer")
	}
	for _, component := range components {
		previous := transaction.Services[component].PreviousTarget
		if err := waitForManagedRuntime(context.Background(), env, component, previous.Identity, ""); err != nil {
			recoveryErrors = append(recoveryErrors, component+" readiness")
		}
	}
	if len(recoveryErrors) > 0 {
		transaction.Status = "recovery-failed"
		transaction.Failure = "managed runtime recovery failed for " + strings.Join(recoveryErrors, ", ")
		transaction.UpdatedAt = env.now()
		_ = writeManagedTransaction(env.files, statePath, &transaction)
		return errors.New(transaction.Failure)
	}
	transaction.Status = "rolled-back"
	transaction.Failure = ""
	transaction.UpdatedAt = env.now()
	if err := writeManagedTransaction(env.files, statePath, &transaction); err != nil {
		return errors.New("pending recovery state could not be persisted")
	}
	if err := env.files.RemoveAll(pendingPath); err != nil {
		return errors.New("pending recovery marker could not be cleared")
	}
	return nil
}

func validateManagedRecoveryTransaction(files managedFileSystem, transactionRoot string, transaction *managedTransaction) ([]string, error) {
	if transaction == nil || len(transaction.Targets) == 0 || len(transaction.Services) == 0 {
		return nil, errors.New("pending transaction is incomplete")
	}
	if !filepath.IsAbs(transaction.UnitDirectory) {
		return nil, errors.New("pending transaction unit directory is invalid")
	}
	for component, target := range transaction.Targets {
		if component != "server" && component != "runner" {
			return nil, errors.New("pending transaction component is invalid")
		}
		service := transaction.Services[component]
		if target == nil || target.Component != component || service == nil || service.Component != component || service.PreviousTarget == nil {
			return nil, fmt.Errorf("pending %s recovery state is incomplete", component)
		}
		if _, err := validateManagedServiceSnapshot(service); err != nil {
			return nil, fmt.Errorf("pending %s service state is invalid", component)
		}
		unitName, _ := managedUnitName(component)
		if !sameManagedPath(service.UnitPath, filepath.Join(transaction.UnitDirectory, unitName)) {
			return nil, fmt.Errorf("pending %s service unit path is invalid", component)
		}
		if err := validateManagedTarget(target); err != nil {
			return nil, fmt.Errorf("pending %s candidate target is invalid", component)
		}
		if service.PreviousTarget.Component != component || validateManagedTarget(service.PreviousTarget) != nil {
			return nil, fmt.Errorf("pending %s previous target is invalid", component)
		}
		wantSnapshot := filepath.Join(transactionRoot, "snapshots", component+".service")
		if !sameManagedPath(service.UnitSnapshot, wantSnapshot) || !files.Exists(wantSnapshot) {
			return nil, fmt.Errorf("pending %s service snapshot is invalid", component)
		}
	}
	if len(transaction.Services) != len(transaction.Targets) {
		return nil, errors.New("pending transaction services are inconsistent")
	}
	components := make([]string, 0, len(transaction.Targets))
	for _, component := range []string{"server", "runner"} {
		if transaction.Targets[component] != nil {
			components = append(components, component)
		}
	}
	return components, nil
}

func managedPointerMatchesTargets(pointer managedPointer, targets map[string]*managedRuntimeTarget, components []string) bool {
	if pointerText(pointer, "status") != "verified" {
		return false
	}
	for _, component := range components {
		target, err := pointerTarget(pointer, component)
		if err != nil || !reflect.DeepEqual(target, targets[component]) {
			return false
		}
	}
	return true
}

func readManagedRecoveryPointer(files managedFileSystem, transactionRoot string, name string) (managedPointer, os.FileMode, error) {
	path := filepath.Join(transactionRoot, "snapshots", name)
	pointer, _, mode, err := readManagedPointer(files, path)
	if err != nil || mode.Perm() != 0o600 || pointerText(pointer, "status") != "verified" {
		return nil, 0, fmt.Errorf("pending recovery %s snapshot is invalid", name)
	}
	return pointer, mode, nil
}

func (updater *managedUpdater) completeVerifiedRecovery(
	ctx context.Context,
	runtimeRoot string,
	transaction *managedTransaction,
	components []string,
) error {
	active, _, _, err := readManagedPointer(updater.env.files, filepath.Join(runtimeRoot, "active.json"))
	if err != nil || pointerText(active, "status") != "verified" {
		return errors.New("verified recovery active pointer is unavailable")
	}
	verified, _, _, err := readManagedPointer(updater.env.files, filepath.Join(runtimeRoot, "verified.json"))
	if err != nil || pointerText(verified, "status") != "verified" {
		return errors.New("verified recovery target pointer is unavailable")
	}
	for _, component := range components {
		activeTarget, activeErr := pointerTarget(active, component)
		verifiedTarget, verifiedErr := pointerTarget(verified, component)
		candidate := transaction.Targets[component]
		if activeErr != nil || verifiedErr != nil || !reflect.DeepEqual(activeTarget, candidate) || !reflect.DeepEqual(verifiedTarget, candidate) {
			return fmt.Errorf("verified %s recovery pointers do not match the candidate", component)
		}
		service := transaction.Services[component]
		unitName, _ := managedUnitName(component)
		unit, _, unitErr := updater.env.files.ReadFile(service.UnitPath)
		fragmentPath, fragmentErr := readManagedSystemdProperty(ctx, updater.env.commands, unitName, "FragmentPath")
		if unitErr != nil || fragmentErr != nil || !sameManagedPath(strings.TrimSpace(fragmentPath), service.UnitPath) || validateManagedUnitTarget(unit, candidate) != nil {
			return fmt.Errorf("verified %s recovery service target could not be confirmed", component)
		}
		if err := verifyManagedEffectiveTarget(ctx, updater.env.commands, unitName, candidate); err != nil {
			return fmt.Errorf("verified %s recovery effective target could not be confirmed", component)
		}
		if err := waitForManagedRuntime(ctx, updater.env, component, candidate.Identity, ""); err != nil {
			return fmt.Errorf("verified %s recovery runtime could not be confirmed", component)
		}
	}
	return nil
}
