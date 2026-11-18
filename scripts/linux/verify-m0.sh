#!/usr/bin/env sh
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)"
python3 -m unittest discover -s tests -v
python3 tools/m0/preflight.py --model "${1:-qwen3.5:4b}"
