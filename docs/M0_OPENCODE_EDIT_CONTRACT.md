# M0.12 — Deterministic native OpenCode editing contract

**Status: CI evidence pending. Overall M0 remains YELLOW.**

## Purpose

The direct-Ollama MVP already performs useful C# repairs, and the M0.11 pilot
already uses real OpenCode for a *text-only* proposal. Neither establishes that
OpenCode's native editing tool can safely edit a disposable workspace.

M0.12 takes one deliberately narrow step: a **deterministic mocked model**
requests one OpenCode `edit` tool call for the frozen `02-build-repair`
fixture. The actual pinned OpenCode 2.3.0 CLI must execute that tool inside a
locked-down container, replacing only `values.Summ()` with `values.Sum()` in a
**copy** of `Program.cs`.

The mock's exact response is fixed and does **not** invoke local Ollama or any
cloud provider. This contract verifies OpenCode's tool-call wire protocol,
permissions and disposable-edit behavior before permitting real Qwen to issue
model-directed edits.

## CI acceptance

`.github/workflows/m0-runtime-image.yml`:

1. Builds the existing offline toolchain image, without the user's repository
   or home as an image build context.
2. Copies only the two frozen C# fixture files into a fresh temporary workspace.
3. Starts OpenCode as non-root with no network, read-only container root, dropped
   capabilities, no new privileges, bounded processes/memory/time and only that
   temporary workspace mounted writable. Its Ollama discovery and chat endpoint
   is a **deterministic loopback mock in the container**.
4. Uses deny-by-default OpenCode permissions; the only edit exception is the
   fixed `Program.cs`. Shell, external-directory, Code Mode and additional
   edits remain denied. Permission rules are defense in depth; Podman/Docker
   isolation provides the OS boundary.
5. Accepts exactly two streaming model requests: one with a native `edit`
   tool call and one after OpenCode has executed the tool. A missing/changed
   native schema, any unexpected route, any other tool or repeated request
   fails the test.
6. Independently compares the resulting disposable source with the one-token
   expected repair; verifies the project file was untouched and no unexpected
   top-level files were created. The frozen original is never mounted.
7. Starts **a second no-network container without a model socket** to run
   the existing trusted offline restore/build/console evaluator. Requires exact
   `Total: 15` output.

## Security and limits

- **This is model-free contract verification.** A passing CI result says the
  pinned OpenCode CLI can consume a valid native edit call in a contained copy;
  it is *not* evidence of Qwen's real tool-use reliability.
- The CI runner uses Docker; rootless Podman on the Dell needs separate
  environment-specific acceptance before model-generated tool execution.
- Do **not** run `tools/m0/benchmark.py` on a normal host. Its
  `--acknowledge-unisolated-m0-risk` flag is not isolation.
- A real-model follow-up must carry over model/digest pinning, strict
  tool-call validation, bounded read/write operations, owner authorization,
  source hashes, independent evaluation, negative and recovery checks.
- M0 stays YELLOW until the original 3×3 task/model protocol and independent
  security evidence pass.

The next slice after this CI contract is real local Qwen requesting a strictly
bounded edit via the verified, isolated OpenCode path. Do not expand shell,
network or filesystem privileges to make the integration pass.
