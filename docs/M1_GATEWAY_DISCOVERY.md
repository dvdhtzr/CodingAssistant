# M1.0 — Read-only Gateway/ACP integration discovery

**Current milestone status:** M0 **GREEN on the Dell at documented evidence assurance**;
M1 **YELLOW / NOT YET SECURITY-QUALIFIED**. Windows host qualification and
M1 end-to-end operational security are separate gates.

The original approved M1 architecture remains:
ASP.NET Core control plane → OpenClaw Gateway → ACP runtime → OpenCode
→ locally pinned Ollama. M0's rootless, no-network OpenCode runner and independent
evaluator stay reusable. Do **not** assume that OpenClaw's own tool restrictions
automatically sandbox a child ACP coding harness.

## First operational step: inventory, not installation

Run this on the Dell:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/discovery.py
```

The reviewed Python script invokes only installed binaries with `--version`:
Node, OpenClaw, OpenCode (host presence informational), and Podman. It
does **not** install software, launch the Gateway or ACP, load OpenClaw
configuration, read environment secrets, contact Ollama, run model code,
or change existing projects. Output contains version tokens and booleans,
not executable paths, provider keys or arbitrary command logs. Evidence is
stored in ignored `artifacts/m1/gateway-discovery.json`.

It may report `M1.0 PREREQUISITES_BLOCKED` if the OpenClaw executable
is absent or host Node is too old. This is a **useful discovery outcome**,
not a failure of the already-qualified M0 or permission to install an
unreviewed CLI. No M1 autonomous coding is enabled by `DISCOVERY_READY`.

### Runtime compatibility verified in current official docs (Nov 19, 2026)

OpenClaw supports Node 24.16.0+ on the 24.x line or Node 26.1.0+
on the 26.x line. **Node 24.22.1+ or 26.12.1+ is strongly recommended
for security**; Node 22 is unsupported. Node 27 development releases
are intentionally not eligible for this initial production Gateway
integration (a more restrictive project choice). The Dell previously
reported Node 22.22.1, so it is **expected** to report an incompatible
host runtime until independently remedied. We do **not** automatically
upgrade the host: a separately pinned rootless Gateway container with
a supported patched Node is a possible M1 implementation path that
avoids disrupting the existing M0 toolchain.

OpenClaw's packaged `@openclaw/acpx` plugin **embeds** the ACP runtime;
there is no requirement to install a standalone `acpx` binary.
The plugin and its Gateway state are **NOT_INSPECTED** by this first
version-only probe. Its harness target id for OpenCode is `opencode`.
Do not silently select Claude/Codex/cloud providers or configure a cloud
fallback. ACP sessions run without interactive terminal permission prompts;
noninteractive permissions and a distinct external OS sandbox must be
verified before any coding turn.

Official source references checked for M1 kickoff:
- https://docs.openclaw.ai/install/node
- https://docs.openclaw.ai/tools/acp-agents/quickstart
- https://docs.openclaw.ai/tools/acp-agents-setup
- https://docs.openclaw.ai/tools/acp-agents

## M1 staged acceptance (no owner design questionnaire)

1. **M1.0:** Version-only discovery and patched Node/OpenClaw packaging
   decision, with model/cloud calls disabled.
2. **M1.1:** Read-only Gateway/embedded ACP plugin/OpenCode harness
   discovery under an explicit control-plane vs execution-plane boundary.
   Verify the actual runtime protocol/version; use **no invented Gateway
   methods** and no writes to original repos.
3. **M1.2:** Read-only coding-plan request with canonical plan version/hash;
   owner approval must exactly match both before execution is permitted.
4. **M1.3:** Single approved worktree-scoped coding turn, independently
   OS-enforced child-process, file and network confinement, exact provider
   pin and explicit denial of external Git remotes.
5. **M1.4:** Ten adversarial security negative tests, durable run events,
   reconnect, cancellation and crash recovery/reconciliation. Every
   security failure disables autonomous execution on that OS.
6. **M1 exit:** Linux *and* Windows need separately validated runtime and
   security gates before cross-platform M1 GREEN. A Dell-only pass cannot
   be used to claim Windows is qualified.

Keep deferred pluggable skills design in the backlog; don't let untrusted
skill prompts or scripts bypass owner approval, exact file scope, or OS
enforcement. No .NET API server, SQLite database, Mission Control UI,
or distributed runner is introduced in M1.0.

## Owner M1.0 outcome — November 19, 2026

The Dell reported `M1.0 PREREQUISITES_BLOCKED`: host Node `v22.22.1`
(unsupported), OpenClaw absent, host OpenCode `v2.3.2`, Podman `5.7.0`.
This is an expected M1 host-compatibility blocker, **not an M0 regression**.
The M0 qualified image retains its own pinned OpenCode/runtime.

### M1.1 — official versioned Gateway image probe (no Gateway startup)

To leave host Node and OpenClaw untouched, this probe uses the official
`ghcr.io/openclaw/openclaw:2026.11.5` image, never moving `latest`.
The official Podman setup requires a host OpenClaw CLI; our initial
**version-only** probe deliberately checks the image without that CLI.

Run on the Dell, after this change is merged:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_image_probe.py --prepare
```

Explicit `--prepare` pulls only this single official versioned image into
the user-owned rootless Podman store. Host Node/OpenClaw packages are NOT
installed or modified. The probe then verifies an immutable image ID and
official GHCR repository digest, and executes only `node --version` and
`node /app/openclaw.mjs --version` with network disabled, no host mounts,
read-only root filesystem, non-root identity, no capabilities and no-new-privileges.
It requires recommended security-patched Node and exact OpenClaw 2026.11.5.
No Gateway/ACP session/model inference is launched. Model state, secrets,
original projects, and worktrees are not mounted or inspected.

After the image is available, an offline rerun needs no pull:

```bash
python3 tools/m1/gateway_image_probe.py
```

Results go into Git-ignored `artifacts/m1/gateway-image-probe.json`.
A `RUNTIME_IMAGE_PASS` only certifies the image's version/startup discovery;
it is NOT a working OpenClaw Gateway or ACP session. If blocked, stop;
do not widen networking or replace host Node merely to get a green probe.

Next M1 stages must start and verify an isolated Gateway using explicitly
reviewed local-only configuration, then validate its embedded ACPX plugin
and the external OpenCode harness. Independent execution-plane isolation,
exact owner plan/hash approval, negative tests and Windows acceptance
remain mandatory.

Current primary references:
- https://docs.openclaw.ai/install/podman
- https://docs.openclaw.ai/install/node
- https://docs.openclaw.ai/tools/acp-agents-setup
- https://github.com/openclaw/openclaw/pkgs/container/openclaw

## M1.1 owner Dell result and M1.2 actual Gateway loopback smoke

**M1.1 RUNTIME_IMAGE_PASS**. On Nov 19 the owner verified the official image with immutable ID `405dcc682726f127d316d435e29d3c5d0cdecb502aef6d74384ce6dd4ffe2700` and official registry digest `ghcr.io/openclaw/openclaw@sha256:97781490d6cd01cec8d1f0ad300ff8bedfaae18ec5b8c30666aaebc470a6309f`. The image runs patched Node `v24.22.1` and OpenClaw `2026.11.5`. No host installation or Gateway process occurred.

**Next owner command after PR green and merge:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_loopback_probe.py
```

Unlike the previous version-only check, this launches a **real temporary OpenClaw Gateway**. It requires exactly the image's already-verified immutable ID/digest and starts it with `gateway --allow-unconfigured --bind loopback --port 18789` inside rootless Podman. The guest has **network=none** (its own loopback remains available), no host-project or config mounts, no host ports, zero capabilities, no new privileges, and read-only root except a disposable `/tmp` tmpfs. A fresh random Gateway token is provided by a private ephemeral environment file, never printed or stored in audit evidence. `HOME` and Gateway state are entirely ephemeral inside the guest. No ACP agent, coding task, remote Git operation, or Ollama inference is configured or requested.

The fixed monitor invokes Node within the isolated guest to request only three documented HTTP probes: `/healthz` (process liveness), `/startupz` (startup completion), and `/readyz` (readiness). **All three must return HTTP 200**. The monitor also requires the Gateway process to remain alive. The container is removed after the check, even on failure; a failed cleanup prevents PASS. Only status codes and safe flags are recorded in ignored `artifacts/m1/gateway-loopback-smoke.json`.

This is a **Gateway runtime test only**, not a working ACP/ACPX connection. If readiness remains 503, keep M1.2 BLOCKED, inspect its explicit local evidence and actual failure cause before adjusting config. Never relax networking or mount your home directory to force a green result. The later true ACP bridge must still have external OS-enforced harness isolation, strict owner approval, explicit local Ollama model pinning and negative security tests.

Command contract sources: https://docs.openclaw.ai/cli/gateway/running and https://docs.openclaw.ai/gateway/health .


## M1.2 owner result — first real isolated Gateway process: GREEN

On November 19, the Dell returned `M1.2 GATEWAY_LOOPBACK_PASS` with
rootless execution, `--network=none`, no host mounts, no published ports,
and independently observed HTTP 200 for `/healthz`, `/startupz`, `/readyz`.
The temporary Gateway was verified removed afterward. No ACP, model,
cloud service or coding agent was involved. Evidence:
`artifacts/m1/gateway-loopback-smoke.json`. Overall M1 remains YELLOW.

## M1.3 — inspect ACPX packaging in the exact pinned Gateway image

OpenClaw upstream tag `v2026.11.5` has plugin metadata at
`extensions/acpx/openclaw.plugin.json` and package
`@openclaw/acpx` version `2026.11.5`, depending on `acpx` version
`0.21.0`. Its Dockerfile uses `OPENCLAW_EXTENSIONS` to select optional
plugins; the root distribution excludes `dist/extensions/acpx/**`.
Consequently a healthy Gateway alone proves nothing about packaged ACPX.

**Next Dell command, after this PR is merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/acpx_image_inventory.py
```

This **reads only five fixed package/manifest paths inside the existing
rootless immutable Gateway image**, by executing trusted built-in Node
filesystem reads in a `podman run --network=none --read-only` container.
No config, host folders, model, API, adapter, or Gateway is accessed.
No pull, package install or `npx`/`npm` execution occurs.

Result is `M1.3 INVENTORY_COMPLETE` plus a separate distribution finding:
`NOT_BUNDLED`, `FILES_PRESENT_NOT_ACTIVATED`, or
`INCOMPLETE_OR_UNEXPECTED`. The first result is anticipated but must be
established on the actual Dell image. An inventory result **cannot**
authorize ACP sessions or coding agents. If the image lacks ACPX, plan
a version-pinned, reviewed derived image or plugin install in a separate
unprivileged build context, rather than enabling network access inside
the Gateway/agent runtime. The ACPX OpenCode adapter itself must be
verified independently, including its spawn command and strict local
model pin and process isolation.

Official version-tagged implementation evidence:
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/Dockerfile`
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/extensions/acpx/package.json`
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/extensions/acpx/openclaw.plugin.json`
- `https://docs.openclaw.ai/tools/acp-agents-setup`


## M1.3 owner Dell inventory — 2026-11-19

Actual M1.3 output: `INVENTORY_COMPLETE`, plugin distribution
`NOT_BUNDLED`. Manifest, plugin entry, plugin package.json, and the
`acpx` dependency were absent in official image
`405dcc682726f127d316d435e29d3c5d0cdecb502aef6d74384ce6dd4ffe2700`.
The official Gateway M1.2 liveness/readiness GREEN is unaffected.
No plugin install, ACP, model or Gateway launch occurred during M1.3.
Evidence: `artifacts/m1/acpx-image-inventory.json`.

## M1.4 — explicit reviewed official-source ACPX build

Source inspection of tag `v2026.11.5` found ACPX plugin
`@openclaw/acpx@2026.11.5` with `acpx@0.21.0`. OpenClaw's official
Dockerfile supports `--build-arg OPENCLAW_EXTENSIONS=acpx`, which
retains the selected plugin runtime/dependencies in the production image.
The reviewed annotated tag currently points to full source commit
`e13cf15ece9bbd969b7362927c315362c90c97ff`.

**Run on Dell after this PR is GREEN and merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/acpx_source_image.py --prepare
```

This is an **explicitly networked build operation**, unlike our previous
offline runtime checks. It fetches *only* the official GitHub source
tag by HTTPS; verifies the expected full commit SHA, clean checkout,
official Dockerfile and ACPX package/dependency versions; then calls
rootless `podman build` with only the official source as build context.
The official Dockerfile will download public build images and package
dependencies (including plugin-declared adapter platform packages).
No cloud AI, Ollama, paid API, token, host npm installation, or execution
of Claude/Codex/OpenCode agents occurs. Public upstream build scripts
DO execute in build containers; this has real supply-chain/compute/disk
risk, and its network access is permitted for this **opt-in build only**.
Allow >=20 GiB free disk; on slower hardware compilation may be substantial.
Build output is saved in a private 0600 ignored log file; original working
repo and official prebuilt Gateway image remain unmodified.

The new image is tagged locally (never pushed) as
`localhost/codingassistant-openclaw-acpx:2026.11.5-e13cf15e`.
The script requires source/revision/ACPX selection labels and an immutable
local image ID. It then uses three fresh **no-network/no-mount/read-only-root**
Podman guests to verify security-patched Node, exact OpenClaw version,
packaged ACPX entry/manifest/package, and pinned `acpx@0.21.0` dependency.
Only then is `M1.4 ACPX_IMAGE_PACKAGED_PASS` reported.

If the image already exists, omit `--prepare` to verify it offline:

```bash
python3 tools/m1/acpx_source_image.py
```

The script keeps append-only audit JSON and a separate local build log
in `artifacts/m1/`, all ignored by Git. A failure is `BLOCKED` with
a precise reason/log location; **do not** patch the upstream checkout,
disable isolation or install arbitrary plugins into the running Gateway.

**Packaging is not activation.** This step does NOT launch a Gateway,
ACP session, plugin service or model, and cannot claim a working OpenCode
adapter. After PASS, next M1 work is to activate the pinned plugin with
strict explicit local-only settings, verify the ACPX/OpenCode adapter command
and enforce a separate OS boundary for every child process. Owner plan
approvals and all M1 negative security tests remain outstanding.

References:
- https://docs.openclaw.ai/docker
- https://github.com/openclaw/openclaw/blob/v2026.11.5/Dockerfile
- https://github.com/openclaw/openclaw/blob/v2026.11.5/extensions/acpx/package.json
- https://docs.openclaw.ai/tools/acp-agents-setup


## M1.4 owner Dell result — November 19, 2026

**M1.4 ACPX_IMAGE_PACKAGED_PASS.** The approved official source
`v2026.11.5` at commit `e13cf15ece9bbd969b7362927c315362c90c97ff`
was built with `OPENCLAW_EXTENSIONS=acpx`. Offline inspection of the
derived image confirmed packaged ACPX runtime components and the pinned
dependency; Gateway/ACP/Ollama were not executed.

Derived immutable image ID:
`5aa8b2911525ccf1d5af5ba73ae4bc5f219cdf2a7c91bdf6cfee7208941c5f0c`

Append-only evidence:
`artifacts/m1/acpx-image-8ec14fd482c4.json`.
Private build log:
`artifacts/m1/acpx-build-19a72f809ab9.log`.

The original official Gateway image, host Node installation and M0
baseline remain unchanged. **M1.4 packaging GREEN; M1 overall YELLOW.**

## M1.5 — Real ACPX lazy-registration smoke (not an ACP session)

The pinned upstream plugin entry at
`extensions/acpx/index.ts` registers its backend **lazily** during
Gateway startup and writes the exact log marker
`embedded acpx runtime backend registered lazily`. The marker is a
gateway-side plugin activation signal; the actual ACP backend and the
OpenCode adapter are NOT initialized or invoked by this test. The
plugin's agent harness lists require broader tool admissions than our
product allows, so **do not dispatch any agent session until the child
process sandbox and tool permissions are independently enforced**.

Next owner command after this PR is GREEN and merged:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/acpx_gateway_activation_probe.py
```

This checks the exact owner-built immutable image ID, the unchanged official
base image provenance, and the packaged plugin version in three offline
guests. It then starts a real temporary OpenClaw Gateway using a disposable,
owner-only token file. The rootless Gateway has network disabled, no host
mounts or published ports, read-only root with disposable tmpfs state,
zero capabilities and no-new-privileges. Channel/canvas integration and
ACPX startup adapter probing are disabled; the plugin itself is *not*
skipped. The test requires `/healthz`, `/startupz`, and `/readyz`
to return HTTP 200 and the exact plugin lazy-registration signal to
appear in the captured logs. Only the boolean observation is saved;
logs and the token are never echoed or persisted as audit evidence.
The disposable container must be fully removed for PASS.

Possible results: `M1.5 ACPX_LAZY_REGISTRATION_PASS` or `M1.5 BLOCKED`.
Neither result grants permission to start an ACP session, install any
adapter, access a real source workspace, use cloud models, or consider M1
complete. If BLOCKED, investigate the specific recorded reason without
enabling network, widening mounts or making changes outside GitHub PRs.

After a PASS, the next gate is the **real Gateway → ACPX → OpenCode
adapter compatibility** on a disposable no-network, no-project container,
followed by explicit local Ollama model pinning and separate OS-enforced
child-process controls before any autonomous source edits.


## M1.5 owner verification — November 19, 2026

Owner ran the merged M1.5 activation smoke against the exact
official-source-derived Gateway+ACPX image:

```text
M1.5 ACPX_LAZY_REGISTRATION_PASS
  verified image: 5aa8b2911525ccf1d5af5ba73ae4bc5f219cdf2a7c91bdf6cfee7208941c5f0c
  offline ACPX metadata recheck: True
  Gateway started: True
  health: {'/healthz': 200, '/startupz': 200, '/readyz': 200}
  plugin backend registered lazily: True
  disposable Gateway removed: True
  ACP session / model / adapter launch: NOT_ATTEMPTED
```

Append-only local evidence:
`artifacts/m1/acpx-lazy-gateway-0b65fa412450.json`.

**M1.5 GREEN** confirms genuine Gateway plugin registration, but it
does **not** prove ACPX can invoke OpenCode, nor that the external adapter
would inherit OpenClaw's own sandboxing. **M1 remains YELLOW.**

## M1.6 — Real read-only Gateway/ACPX OpenCode discovery

Review of **pinned** OpenClaw tag `v2026.11.5` and its `acpx@0.21.0`
dependency established two exact contracts:

- The plugin registers Gateway RPC **`acpx.agents.list`** with
  `operator.read` scope and no parameters; this is a metadata-only
  request and does not create an ACP session.
- The pinned ACPX agent registry selects an installed
  **`opencode acp`** binary if available but has an unsafe-for-us
  package-exec fallback `npx -y opencode-ai acp`. **This workflow
  NEVER runs the fallback** and will not grant runtime network access
  or implicitly install OpenCode.

The next test starts one short-lived Gateway from the already verified
immutable image under the same rootless/no-network/no-host-mount/no-port
controls. It independently inspects the actual pinned agent registry via
read-only Node imports, then invokes **only the approved
`acpx.agents.list` RPC** using the bundled Gateway CLI and a private
disposable token. It confirms that the Gateway-reported `opencode`
installation matches the independent registry's installation status.
Only sanitized metadata is recorded; raw CLI output, token, logs and
device metadata are never retained. The container is always removed.
No ACP session, adapter spawn, model inference, provider configuration,
source modification, or internet access is allowed.

**Run on the Dell once PR is GREEN and merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/acpx_opencode_rpc_probe.py
```

`M1.6 ACPX_OPENCODE_DISCOVERY_PASS` means the real Gateway's
authenticated plugin metadata path works; it will also report whether
OpenCode is **`missing`** or **`installed`** in this image.
A missing adapter is a **valid discovery**, not a successful ACP
handshake and not permission to enable `npx`. If blocked, examine the
recorded reason before changing Gateway credentials or permissions.
Actual ACP initialize/session operations and OS-constrained OpenCode
child process are still future gated work.

Source references:
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/extensions/acpx/index.ts`
- `https://github.com/openclaw/acpx/blob/v0.21.0/src/agent-registry.ts`
- `https://github.com/openclaw/openclaw/blob/v2026.11.5/docs/cli/gateway/query.md`
