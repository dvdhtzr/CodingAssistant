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
