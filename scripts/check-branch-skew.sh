#!/usr/bin/env bash
# Pre-push guard: CI runs the PR merged with master, so local checks on a
# skewed branch can pass while CI fails. Block the push only when origin/master
# changed files this branch also touches.
set -euo pipefail

branch=$(git rev-parse --abbrev-ref HEAD)
if [ "$branch" = "master" ] || [ "$branch" = "HEAD" ]; then
  exit 0
fi

git fetch --quiet origin master || true
git rev-parse --verify --quiet origin/master >/dev/null || exit 0

base=$(git merge-base HEAD origin/master)
if [ "$base" = "$(git rev-parse origin/master)" ]; then
  exit 0
fi

overlap=$(comm -12 \
  <(git diff --name-only "$base" origin/master -- | sort) \
  <(git diff --name-only "$base" HEAD -- | sort))

if [ -z "$overlap" ]; then
  exit 0
fi

{
  echo "Branch is behind origin/master, and master changed files this branch also touches:"
  echo "$overlap" | sed 's/^/  /'
  echo "Local checks ran on a tree CI will never test. Run 'git merge origin/master',"
  echo "resolve, re-run checks, then push."
} >&2
exit 1
