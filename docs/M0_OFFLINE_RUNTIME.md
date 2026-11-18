# M0.7 — Offline OpenCode/.NET/Node toolchain

**Prerequisites:** Dell M0.3–M0.6 passed (owner-reported), including one real pinned inference through rootless Podman with all eight M0.6 checks. M0.7 deliberately **does not run an agent**. It proves that the required executables exist inside a separate rootless container while public networking is disabled.

## One-time build on Dell

From Konsole, one command:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/runtime_image_probe.py --build
```

**The `--build` flag authorizes a network-using rootless image build.** It downloads two official base images and the pinned `@opencode/cli@2.3.0` npm package, installs Git, and produces `localhost/codingassistant-m0-runtime:2.3.0`. The image can be large because it contains the .NET 10 SDK. The build context is only `infra/runtime/` with a restrictive `.containerignore`—never your repository, home directory or model evidence. It never uses sudo. This is a one-time toolchain installation inside Podman, *not* a system-wide installation.

After building, the runner captures the local immutable image ID and starts the container by that ID, with `--pull=never`, `--network=none`, `--read-only`, no capabilities, no-new-privileges, explicit normal UID/GID, process/memory limits, and **no host mounts**. The only writable space is a disposable tmpfs. It checks `dotnet --version`, `node --version`, `npm --version`, `git --version`, `opencode --version`, and `opencode run --help`. No model request, OpenCode session, tool execution or repository changes occur.

Expected: `M0.7 OFFLINE TOOLCHAIN TOOLCHAIN_PASS` and six `PASS` checks. Logs and evidence are **local and ignored by Git** under `artifacts/m0/`. A nonzero exit, missing executable or version mismatch means **BLOCKED**. Share only the printed check statuses and reason; never post machine secrets or full system inventories.

If a compatible image has already been built, `python3 tools/m0/runtime_image_probe.py` re-verifies it without any downloads.

## Limits and future requirements

- The image recipe pins the OpenCode version to 2.3.0, but the upstream Node 22 and .NET 10 base tags and transitive npm dependencies are **not yet pinned by manifest digests/lockfile**. The runner records the resulting local image ID; image reproducibility and supply-chain review remain a pre-coding gate. No `TOOLCHAIN_PASS` should be mistaken for hardened runtime certification.
- OpenCode has **not** communicated with Ollama from inside this image. The M0.6 bridge intentionally accepts one fixed prompt; it must be upgraded to a bounded, model-pinned OpenAI-compatible API with streaming and tool calls for real OpenCode.
- We must still run the negative security and recovery tests and independent acceptance checks in an isolated disposable workspace before allowing generated code to execute. The old `tools/m0/benchmark.py --acknowledge-unisolated-m0-risk` is not a safe substitute.

Official references: https://opencode.ai/docs/cli/commands, https://opencode.ai/docs/providers, https://dotnet.microsoft.com/download/dotnet/10.0, https://docs.podman.io/en/stable/markdown/podman-run.1.html.

## Podman image ID compatibility (M0.7 corrective)

Podman normally prints a bare 64-character hexadecimal image ID for `podman image inspect --format '{{.Id}}'`. The initial verifier accepted only `sha256:`-prefixed IDs and rejected a valid locally built image before running its tool checks. Both full-length immutable ID forms are now accepted, while short IDs, image tags, and malformed input remain blocked.

**If your image was already built successfully and only image-ID validation failed, do not rebuild.** After pulling this fix, verify the existing local image with:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m0/runtime_image_probe.py
```

Only pass `--build` if the image genuinely does not exist or if you intentionally want to rebuild it.

## M0.7 diagnostic follow-up

A Dell operator run found all six version checks marked FAIL after a built image was accepted. The evidence identifies the startup failure: Podman exited **126** because `--workdir=/workspace` referenced a directory absent from the existing local image; no tool version commands ran. The runtime probe now uses `/tmp`, the existing disposable writable tmpfs, rather than assuming `/workspace` exists.

**Dell tmpfs follow-up (Nov 18):** After that fix, the same image still failed before commands ran with `crun: write: No space left on device`, despite ample free space and inodes on the host. The M0.4 rootless Python isolation preflight still passed all seven checks. An otherwise equivalent minimum M0.7 image-start probe failed with normal tmpfs copy-up and printed `M0.7_EMPTY_TMPFS_START_PASS` with `--tmpfs=/tmp:rw,nosuid,nodev,size=64m,notmpcopyup`. This is direct evidence that image `/tmp` copy-up triggered the startup failure under the tested conditions.

The M0.7 Podman verifier now mounts an **empty** disposable tmpfs with `notmpcopyup` while keeping its original 512 MB size, no host mounts, no host/public network, read-only root filesystem, dropped capabilities, and no-new-privileges. The Docker CI smoke continues using its own compatible tmpfs flags (the `notmpcopyup` option is specific to Podman). **No image rebuild is needed**; rerun the existing local image **without** `--build` after pulling the fix. The CLI reports the process exit code and bounded stdout/stderr for any further diagnosis. A passing toolchain check still does not authorize coding-agent execution.

## Dell qualification result (November 18, 2026)

The owner reported `M0.7 OFFLINE TOOLCHAIN TOOLCHAIN_PASS` from rootless Podman after PR #10 (`a7c64bc`): six tool checks PASS; container exit code `0`; local ignored evidence: `artifacts/m0/runtime-image.json`. **M0.7 is GREEN for offline toolchain startup on the Dell.** This is not approval to start OpenCode coding sessions. The environment has not yet demonstrated OpenCode-to-Ollama inference through its actual protocol, worktree-scoped coding safety, complete negative/recovery gates, or 3×3 independent coding benchmarks. M0 overall remains YELLOW. No further image build is required for this qualification.
