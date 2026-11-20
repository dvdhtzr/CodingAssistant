#!/usr/bin/env bash
# CodingAssistant: one stable command, repeatable online or offline.
# The public GitHub Release download and local security checks are handled
# by the Python launcher. This wrapper only updates the clean main checkout.
set -u
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$root" || exit 2

if [[ "$(git branch --show-current)" == "main" ]]; then
  echo "Checking for CodingAssistant updates..."
  if ! GIT_TERMINAL_PROMPT=0 git pull --ff-only --quiet origin main; then
    echo "Cannot update right now; using the last installed version if available."
  fi
else
  echo "Not on main: keeping the current checkout unchanged."
fi

# Request access to the *fixed disposable fixture* only. The launcher runs
# rootless model-free preflight and disables coding on any failed gate.
# No model begins until its independently pinned execution plan is separately
# approved and then explicitly started in Mission Control.
exec python3 "$root/tools/m3/launch.py" --enable-execution
