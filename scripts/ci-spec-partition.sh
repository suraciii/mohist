#!/usr/bin/env bash

set -euo pipefail

die() {
  printf 'ci-spec-partition: %s\n' "$*" >&2
  exit 2
}

usage() {
  cat >&2 <<'EOF'
usage:
  ci-spec-partition.sh plan <apphost> <partition-index> <partition-count> <manifest-dir>
  ci-spec-partition.sh run <apphost> <partition-index> <partition-count> <manifest-dir> <report>
  ci-spec-partition.sh verify <download-dir>
EOF
  exit 2
}

require_integer() {
  local value="$1"
  local name="$2"
  [[ "$value" =~ ^[0-9]+$ ]] || die "$name must be a non-negative integer"
}

partition_paths() {
  local manifest_dir="$1"
  all_classes_file="$manifest_dir/all-classes.txt"
  selected_classes_file="$manifest_dir/selected-classes.txt"
  metadata_file="$manifest_dir/partition.txt"
}

plan_partition() {
  local apphost="$1"
  local partition_index="$2"
  local partition_count="$3"
  local manifest_dir="$4"

  [[ -x "$apphost" ]] || die "apphost is not executable: $apphost"
  require_integer "$partition_index" partition-index
  require_integer "$partition_count" partition-count
  (( partition_count > 0 )) || die 'partition-count must be greater than zero'
  (( partition_index < partition_count )) || die 'partition-index must be less than partition-count'

  partition_paths "$manifest_dir"
  mkdir -p "$manifest_dir"

  local discovered
  if ! discovered="$("$apphost" -list classes -noColor -noLogo -noAutoReporters)"; then
    die 'xUnit class discovery failed'
  fi

  local -a raw_classes=()
  while IFS= read -r class_name; do
    class_name="${class_name%$'\r'}"
    [[ -z "$class_name" ]] && continue
    [[ "$class_name" == Mohist.Server.SpecTests.* ]] || die "unexpected discovered class: $class_name"
    [[ "$class_name" != *[[:space:]]* ]] || die "discovered class contains whitespace: $class_name"
    raw_classes+=("$class_name")
  done <<< "$discovered"

  (( ${#raw_classes[@]} > 0 )) || die 'class discovery returned no classes'

  local duplicate
  duplicate="$(printf '%s\n' "${raw_classes[@]}" | LC_ALL=C sort | uniq -d)"
  [[ -z "$duplicate" ]] || die "class discovery returned duplicate classes: $duplicate"

  local -a classes=()
  mapfile -t classes < <(printf '%s\n' "${raw_classes[@]}" | LC_ALL=C sort)

  local -a selected=()
  local index
  for index in "${!classes[@]}"; do
    if (( index % partition_count == partition_index )); then
      selected+=("${classes[index]}")
    fi
  done
  (( ${#selected[@]} > 0 )) || die "partition $partition_index has no classes"

  printf '%s\n' "${classes[@]}" > "$all_classes_file"
  printf '%s\n' "${selected[@]}" > "$selected_classes_file"
  {
    printf 'index=%s\n' "$partition_index"
    printf 'count=%s\n' "$partition_count"
    printf 'total_classes=%s\n' "${#classes[@]}"
    printf 'selected_classes=%s\n' "${#selected[@]}"
  } > "$metadata_file"

  printf 'Spec partition %s/%s: %s of %s classes\n' \
    "$((partition_index + 1))" "$partition_count" "${#selected[@]}" "${#classes[@]}"
}

run_partition() {
  local apphost="$1"
  local partition_index="$2"
  local partition_count="$3"
  local manifest_dir="$4"
  local report="$5"

  plan_partition "$apphost" "$partition_index" "$partition_count" "$manifest_dir"
  partition_paths "$manifest_dir"

  local -a class_args=()
  local class_name
  while IFS= read -r class_name; do
    class_args+=('-class' "$class_name")
  done < "$selected_classes_file"

  mkdir -p "$(dirname "$report")"
  "$apphost" -noColor -noLogo -noAutoReporters -trx "$report" "${class_args[@]}" \
    2>&1 | tee "$manifest_dir/spec.log"

  [[ -s "$report" ]] || die "xUnit did not write a TRX report: $report"
  local summary
  summary="$(grep -E 'Total:[[:space:]]*[0-9]+' "$manifest_dir/spec.log" | tail -n 1 || true)"
  [[ "$summary" =~ Total:[[:space:]]*[1-9][0-9]* ]] || \
    die 'xUnit completed without executing any tests'
}

verify_partitions() {
  local download_dir="$1"
  [[ -d "$download_dir" ]] || die "artifact directory does not exist: $download_dir"

  local -a partition_dirs=()
  mapfile -t partition_dirs < <(find "$download_dir" -mindepth 1 -maxdepth 1 -type d -print | LC_ALL=C sort)
  (( ${#partition_dirs[@]} > 0 )) || die 'no partition artifacts were downloaded'

  local temporary_dir
  temporary_dir="$(mktemp -d)"
  trap "rm -rf -- '$temporary_dir'" EXIT

  local canonical_all=''
  local declared_count=''
  local dir
  local index
  local count
  local all_file
  local selected_file
  local metadata_file
  local -a indexes=()
  : > "$temporary_dir/selected-classes.txt"

  for dir in "${partition_dirs[@]}"; do
    all_file="$dir/all-classes.txt"
    selected_file="$dir/selected-classes.txt"
    metadata_file="$dir/partition.txt"
    [[ -f "$all_file" && -f "$selected_file" && -f "$metadata_file" ]] || \
      die "partition artifact is incomplete: $dir"

    if [[ -z "$canonical_all" ]]; then
      canonical_all="$all_file"
    else
      cmp -s "$canonical_all" "$all_file" || die 'partitions discovered different class lists'
    fi

    index="$(sed -n 's/^index=//p' "$metadata_file")"
    count="$(sed -n 's/^count=//p' "$metadata_file")"
    require_integer "$index" index
    require_integer "$count" count
    (( count > 0 )) || die 'partition metadata has a zero count'
    if [[ -z "$declared_count" ]]; then
      declared_count="$count"
    else
      [[ "$declared_count" == "$count" ]] || die 'partitions declare different counts'
    fi
    (( index < count )) || die "partition index $index is outside count $count"
    indexes+=("$index")
    cat "$selected_file" >> "$temporary_dir/selected-classes.txt"
  done

  [[ "$declared_count" == "${#partition_dirs[@]}" ]] || \
    die "expected $declared_count partition artifacts, found ${#partition_dirs[@]}"

  local duplicate_indexes
  duplicate_indexes="$(printf '%s\n' "${indexes[@]}" | LC_ALL=C sort -n | uniq -d)"
  [[ -z "$duplicate_indexes" ]] || die "duplicate partition indexes: $duplicate_indexes"

  local missing_index
  local found_index
  local found
  for ((missing_index = 0; missing_index < declared_count; missing_index++)); do
    found=false
    for found_index in "${indexes[@]}"; do
      if [[ "$found_index" == "$missing_index" ]]; then
        found=true
        break
      fi
    done
    if [[ "$found" != true ]]; then
      die "missing partition index: $missing_index"
    fi
  done

  local duplicate_classes
  duplicate_classes="$(LC_ALL=C sort "$temporary_dir/selected-classes.txt" | uniq -d)"
  [[ -z "$duplicate_classes" ]] || die "classes selected more than once: $duplicate_classes"

  LC_ALL=C sort -u "$canonical_all" > "$temporary_dir/all-classes.sorted"
  LC_ALL=C sort "$temporary_dir/selected-classes.txt" > "$temporary_dir/selected-classes.sorted"
  cmp -s "$temporary_dir/all-classes.sorted" "$temporary_dir/selected-classes.sorted" || \
    die 'selected class union does not equal complete discovered class list'

  printf 'Spec partition coverage verified: %s classes across %s partitions\n' \
    "$(wc -l < "$temporary_dir/all-classes.sorted")" "$declared_count"
}

[[ $# -gt 0 ]] || usage
case "$1" in
  plan)
    [[ $# -eq 5 ]] || usage
    plan_partition "$2" "$3" "$4" "$5"
    ;;
  run)
    [[ $# -eq 6 ]] || usage
    run_partition "$2" "$3" "$4" "$5" "$6"
    ;;
  verify)
    [[ $# -eq 2 ]] || usage
    verify_partitions "$2"
    ;;
  *)
    usage
    ;;
esac
