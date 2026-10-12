#!/usr/bin/env bash
# SPDX-License-Identifier: Apache-2.0
#
# Renders the committed HTML and PDF copies of the docs listed below from their
# Markdown source with quire (pandoc + WeasyPrint, Mermaid diagrams drawn as SVG),
# using the squalor profile. Run from anywhere: ./scripts/render-docs.sh
# Regenerate only through a queued roadmap item; see "Docs" in AGENTS.md.
set -euo pipefail

# Docs under docs/ to render, without the .md extension.
DOCS=(how-it-works)

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v quire >/dev/null 2>&1; then
    echo "render-docs: quire is not on PATH" >&2
    exit 1
fi

for doc in "${DOCS[@]}"; do
    src="$root/docs/$doc.md"
    for ext in pdf html; do
        quire --config squalor --resource-path "$root/docs" "$src" -o "$root/docs/$doc.$ext"
        echo "render-docs: docs/$doc.$ext"
    done
done
