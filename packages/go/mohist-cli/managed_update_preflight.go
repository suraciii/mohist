package mohistcli

import (
	"errors"
	"fmt"
	"path/filepath"
	"reflect"
	"strings"
)

// readManagedReleaseIdentityFile reads a RuntimeIdentity manifest. A missing or
// malformed manifest is an error. The boolean reports that the payload had no
// schemaVersion and is therefore legacy v0; callers that require the canonical
// v1 contract (staged candidates and activated releases) must reject it.
func readManagedReleaseIdentityFile(files managedFileSystem, path string, component string) (managedRuntimeIdentity, bool, error) {
	value, _, err := files.ReadFile(path)
	if err != nil {
		return managedRuntimeIdentity{}, false, fmt.Errorf("managed %s release manifest is unavailable", component)
	}
	identity, legacy, err := readManagedIdentityDocument(value)
	if err != nil {
		return managedRuntimeIdentity{}, false, fmt.Errorf("managed %s release manifest is invalid", component)
	}
	if !legacy && !validManagedRuntimeIdentity(identity) {
		return managedRuntimeIdentity{}, false, fmt.Errorf("managed %s release manifest is not canonical", component)
	}
	return identity, legacy, nil
}

// validateManagedInstalledReleaseManifest checks the release manifest on disk
// against the pointer target identity before any build or service mutation.
func validateManagedInstalledReleaseManifest(files managedFileSystem, target *managedRuntimeTarget) error {
	if target == nil {
		return errors.New("managed release target is unavailable")
	}
	path, err := managedTargetIdentityPath(target)
	if err != nil {
		return err
	}
	identity, _, err := readManagedReleaseIdentityFile(files, path, target.Component)
	if err != nil {
		return err
	}
	if differences := managedIdentityDifferences(identity, target.Identity); len(differences) > 0 {
		return fmt.Errorf(
			"managed %s release manifest does not match its pointer target in %s",
			target.Component, strings.Join(differences, ", "),
		)
	}
	return nil
}

// validateManagedStagedCandidate checks a staged candidate manifest against the
// captured source and transaction generation before promotion or activation.
// The on-disk manifest is authoritative: a canonical identity that disagrees
// with the captured source, the transaction generation, or the staged target is
// rejected before any release is installed or service changed.
func validateManagedStagedCandidate(
	files managedFileSystem,
	candidateRoot string,
	target *managedRuntimeTarget,
	source managedSource,
	generation int64,
) error {
	if target == nil {
		return errors.New("managed staged candidate is unavailable")
	}
	identity, legacy, err := readManagedReleaseIdentityFile(
		files, filepath.Join(candidateRoot, "runtime-identity.json"), target.Component,
	)
	if err != nil {
		return err
	}
	if legacy {
		return fmt.Errorf("managed %s staged candidate manifest is not canonical", target.Component)
	}
	if identity.Component != target.Component {
		return fmt.Errorf("managed %s staged candidate reports component %q", target.Component, identity.Component)
	}
	if identity.SourceRevision != source.Commit {
		return fmt.Errorf("managed %s staged candidate sourceRevision does not match the captured commit", target.Component)
	}
	if identity.TreeHash != source.TreeHash {
		return fmt.Errorf("managed %s staged candidate treeHash does not match the captured tree", target.Component)
	}
	if identity.Generation != generation {
		return fmt.Errorf("managed %s staged candidate generation does not match the update transaction", target.Component)
	}
	if identity.BuildGitHash != source.Commit {
		return fmt.Errorf("managed %s staged candidate buildGitHash does not match the captured commit", target.Component)
	}
	if !reflect.DeepEqual(identity, target.Identity) {
		return fmt.Errorf("managed %s staged manifest does not match the staged target identity", target.Component)
	}
	return nil
}

// validateManagedLiveIdentity checks the observed live runtime against the
// active target identity before activation. A canonical v1 target requires a
// canonical v1 observation (the v1 contract is never relaxed); a bounded v0
// legacy target only has to agree field for field. A malformed observation or
// any identity field disagreement rejects the update.
func validateManagedLiveIdentity(component string, observation managedRuntimeObservation, expected managedRuntimeIdentity) error {
	if validManagedRuntimeIdentity(expected) && !validManagedRuntimeIdentity(observation.Identity) {
		return fmt.Errorf("managed %s live runtime identity is not canonical", component)
	}
	if differences := managedIdentityDifferences(observation.Identity, expected); len(differences) > 0 {
		return fmt.Errorf("managed %s live runtime does not match the active target in %s", component, strings.Join(differences, ", "))
	}
	return nil
}
