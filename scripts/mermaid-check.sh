#!/usr/bin/env bash
# Parses every mermaid block of the given markdown files (DD diagrams must render on GitHub).
# Needs node; installs mermaid + jsdom once into a cache folder outside the repository.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
prefix="${MERMAID_PREFIX:-$HOME/.cache/simnetgateway-mermaid}"
if [ ! -d "$prefix/node_modules/mermaid" ]; then
  mkdir -p "$prefix"
  (cd "$prefix" && npm install --silent mermaid@11 jsdom >/dev/null)
fi
MERMAID_PREFIX="$prefix" node "$here/mermaid-check.mjs" "$@"
