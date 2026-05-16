#!/usr/bin/env bash
#
# Activate the repo-local git hooks (hooks/pre-push runs the test suite
# before every push). git core.hooksPath isn't tracked in the repo so
# every fresh clone needs this one-time setup.
#
# Usage:
#   scripts/setup-hooks.sh

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

git config core.hooksPath hooks
echo "core.hooksPath -> hooks (active hooks: $(ls hooks 2>/dev/null | tr '\n' ' '))"
