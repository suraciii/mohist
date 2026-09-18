package mohistcli

import (
	"encoding/json"
	"os"
	"reflect"
	"sort"
	"testing"
)

// canonicalRuntimeIdentityKeys is the exact nine-field RuntimeIdentity v1 key
// set pinned by fixtures/runtime-identity.v1.json. The fixture is the shared
// contract across Go, TypeScript, and C#; every conformance suite duplicates
// this list on purpose so a drift in any language breaks its own tests.
var canonicalRuntimeIdentityKeys = []string{
	"schemaVersion", "component", "sourceRevision", "buildGitHash", "treeHash",
	"artifactDigest", "releaseId", "generation", "runnerId",
}

const canonicalRuntimeIdentityFixture = "../../../fixtures/runtime-identity.v1.json"

func readCanonicalRuntimeIdentityFixture(t *testing.T) ([]byte, map[string]json.RawMessage) {
	t.Helper()
	value, err := os.ReadFile(canonicalRuntimeIdentityFixture)
	if err != nil {
		t.Fatalf("read canonical RuntimeIdentity fixture: %v", err)
	}
	var object map[string]json.RawMessage
	if err := json.Unmarshal(value, &object); err != nil {
		t.Fatalf("canonical RuntimeIdentity fixture is not a JSON object: %v", err)
	}
	return value, object
}

func sortedCanonicalRuntimeIdentityKeys(t *testing.T) []string {
	t.Helper()
	_, object := readCanonicalRuntimeIdentityFixture(t)
	keys := make([]string, 0, len(object))
	for key := range object {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

func TestCanonicalFixtureHasExactNineFieldKeySet(t *testing.T) {
	got := sortedCanonicalRuntimeIdentityKeys(t)
	want := append([]string(nil), canonicalRuntimeIdentityKeys...)
	sort.Strings(want)
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("canonical fixture keys = %#v, want exact canonical keys %#v", got, want)
	}
}

func TestCanonicalFixtureDeclaresCanonicalFieldTypesAndMeanings(t *testing.T) {
	value, _ := readCanonicalRuntimeIdentityFixture(t)
	var decoded map[string]any
	if err := json.Unmarshal(value, &decoded); err != nil {
		t.Fatalf("canonical fixture is invalid JSON: %v", err)
	}

	for _, field := range []string{"component", "sourceRevision", "buildGitHash", "treeHash", "artifactDigest", "releaseId", "runnerId"} {
		if _, ok := decoded[field].(string); !ok {
			t.Fatalf("canonical fixture %s type = %T, want string", field, decoded[field])
		}
	}
	for _, field := range []string{"schemaVersion", "generation"} {
		number, ok := decoded[field].(float64)
		if !ok || number != float64(int64(number)) {
			t.Fatalf("canonical fixture %s = %v (%T), want integer", field, decoded[field], decoded[field])
		}
	}

	var identity managedRuntimeIdentity
	if err := json.Unmarshal(value, &identity); err != nil {
		t.Fatalf("canonical fixture does not parse into managedRuntimeIdentity: %v", err)
	}
	if identity.SchemaVersion != 1 || identity.Component != "runner" ||
		identity.SourceRevision == "" || identity.BuildGitHash == "" || identity.TreeHash == "" ||
		identity.ArtifactDigest == "" || identity.ReleaseID == "" || identity.Generation <= 0 || identity.RunnerID == "" {
		t.Fatalf("canonical fixture identity = %#v, want a complete RuntimeIdentity v1", identity)
	}
	if identity.BuildGitHash != identity.SourceRevision {
		t.Fatalf("canonical fixture buildGitHash = %q, want managed builds to equal sourceRevision %q", identity.BuildGitHash, identity.SourceRevision)
	}
	if !validManagedRuntimeIdentity(identity) {
		t.Fatalf("validManagedRuntimeIdentity rejected the canonical fixture: %#v", identity)
	}
}

func TestCanonicalFixtureNegativeCases(t *testing.T) {
	_, object := readCanonicalRuntimeIdentityFixture(t)
	canonical := func(t *testing.T) managedRuntimeIdentity {
		t.Helper()
		value, _ := readCanonicalRuntimeIdentityFixture(t)
		var identity managedRuntimeIdentity
		if err := json.Unmarshal(value, &identity); err != nil {
			t.Fatalf("canonical fixture does not parse: %v", err)
		}
		return identity
	}

	t.Run("unknown schemaVersion", func(t *testing.T) {
		identity := canonical(t)
		identity.SchemaVersion = 2
		if validManagedRuntimeIdentity(identity) {
			t.Fatal("validManagedRuntimeIdentity accepted schemaVersion 2")
		}
	})

	t.Run("wrong type", func(t *testing.T) {
		raw := cloneCanonicalRuntimeIdentityRaw(object)
		raw["generation"] = json.RawMessage(`"42"`)
		value, err := json.Marshal(raw)
		if err != nil {
			t.Fatal(err)
		}
		var identity managedRuntimeIdentity
		if err := json.Unmarshal(value, &identity); err == nil {
			t.Fatal("managedRuntimeIdentity accepted a string generation")
		}
	})

	t.Run("missing required field", func(t *testing.T) {
		raw := cloneCanonicalRuntimeIdentityRaw(object)
		delete(raw, "buildGitHash")
		value, err := json.Marshal(raw)
		if err != nil {
			t.Fatal(err)
		}
		var identity managedRuntimeIdentity
		if err := json.Unmarshal(value, &identity); err != nil {
			t.Fatal(err)
		}
		if identity.BuildGitHash != "" {
			t.Fatalf("removed buildGitHash was populated: %#v", identity)
		}
		if validManagedRuntimeIdentity(identity) {
			t.Fatal("validManagedRuntimeIdentity accepted a payload missing buildGitHash")
		}
	})

	t.Run("legacy gitHash-only input", func(t *testing.T) {
		var identity managedRuntimeIdentity
		if err := json.Unmarshal([]byte(`{"gitHash":"legacy-sha"}`), &identity); err != nil {
			t.Fatal(err)
		}
		if identity.BuildGitHash != "" || identity.SourceRevision != "" {
			t.Fatalf("legacy gitHash populated canonical fields: %#v", identity)
		}
		if validManagedRuntimeIdentity(identity) {
			t.Fatal("validManagedRuntimeIdentity accepted a legacy gitHash-only payload")
		}
	})
}

func TestGoReleaseWriterMatchesCanonicalFixtureKeySet(t *testing.T) {
	value, _ := readCanonicalRuntimeIdentityFixture(t)
	var fixture managedRuntimeIdentity
	if err := json.Unmarshal(value, &fixture); err != nil {
		t.Fatalf("canonical fixture does not parse: %v", err)
	}
	fixture.Version = "0.0.0+" + fixture.SourceRevision

	files := newManagedBuildTestFiles()
	source := managedSource{
		RepositoryRoot: "/repo", Commit: fixture.SourceRevision, TreeHash: fixture.TreeHash,
		SnapshotRoot: "/managed-runtime/transactions/conformance/snapshot",
		BuildRoot:    "/managed-runtime/transactions/conformance/build/source",
	}
	if err := writeManagedMetadata(files, "/candidate/runner", source, fixture); err != nil {
		t.Fatalf("writeManagedMetadata() error = %v", err)
	}

	fixtureKeys := sortedCanonicalRuntimeIdentityKeys(t)
	for _, path := range []string{"runtime-identity.json", "release.json", "dist/build-info.json"} {
		raw := readManagedMetadataBytes(t, files, "/candidate/runner/"+path)
		if path == "release.json" {
			var release map[string]json.RawMessage
			if err := json.Unmarshal(raw, &release); err != nil {
				t.Fatalf("release.json is invalid JSON: %v", err)
			}
			raw = release["identity"]
		}
		if got := canonicalRuntimeIdentityKeysOf(t, raw); !reflect.DeepEqual(got, fixtureKeys) {
			t.Fatalf("%s canonical keys = %#v, want fixture keys %#v", path, got, fixtureKeys)
		}
	}

	producedValue := readManagedMetadataBytes(t, files, "/candidate/runner/runtime-identity.json")
	var produced managedRuntimeIdentity
	if err := json.Unmarshal(producedValue, &produced); err != nil {
		t.Fatalf("writer identity is invalid JSON: %v", err)
	}
	produced.Version = fixture.Version
	if produced != fixture {
		t.Fatalf("writer identity = %#v, want fixture identity %#v", produced, fixture)
	}
}

func cloneCanonicalRuntimeIdentityRaw(source map[string]json.RawMessage) map[string]json.RawMessage {
	clone := make(map[string]json.RawMessage, len(source))
	for key, value := range source {
		clone[key] = append(json.RawMessage(nil), value...)
	}
	return clone
}

// canonicalRuntimeIdentityKeysOf returns the sorted canonical keys of an
// identity object, dropping the optional `version` display metadata that is
// not part of identity.
func canonicalRuntimeIdentityKeysOf(t *testing.T, value []byte) []string {
	t.Helper()
	var object map[string]json.RawMessage
	if err := json.Unmarshal(value, &object); err != nil {
		t.Fatalf("identity JSON is invalid: %v", err)
	}
	delete(object, "version")
	keys := make([]string, 0, len(object))
	for key := range object {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

func readManagedMetadataBytes(t *testing.T, files *managedBuildTestFiles, path string) []byte {
	t.Helper()
	value, _, err := files.ReadFile(path)
	if err != nil {
		t.Fatalf("ReadFile(%q) error = %v", path, err)
	}
	return value
}
