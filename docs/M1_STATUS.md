# M1 secure orchestration — status

**Status (November 19, 2026): YELLOW / M1.0 read-only host discovery prepared.**

## Accepted M0 baseline

Owner-run frozen benchmark: real local `qwen3.5:4b`, native OpenCode
`write`, verified independent rootless Podman compile/test in **all 9/9**
fresh task/repetition attempts. Model-free unit and container test harnesses
passed prior to deployment.

Independent owner Dell audit: `M0.18 EVIDENCE_REVIEW_PASS`, all nine
`EVIDENCE_PASS`, and `Recorded evidence audited: QUALIFIED`.
Preserved local results and append-only independent-audit evidence paths:

- `artifacts/m0/evaluation-20261119T161744Z-83706eec/results.json`
- `artifacts/m0/evaluation-20261119T161744Z-83706eec/independent-safety-audit-2b967f74b002.json`

Limitations remain those documented in
`docs/M0_INDEPENDENT_EVIDENCE_AUDIT.md`; this is not blanket security
certification, independent Windows-host acceptance, or permission to
launch an unrestricted Gateway or coding harness.

## Current M1.0 slice

Version-only `tools/m1/discovery.py` inventory on each host (Dell first).
It detects installed Node, OpenClaw, Podman and informational host OpenCode
versions without installing, changing config, connecting to inference,
starting a Gateway, or invoking ACP. Missing binaries or incompatible Node
produce `PREREQUISITES_BLOCKED`, not a security bypass.

Current official OpenClaw runtime: Node 24.16.0+ or Node 26.1.0+, with
24.22.1+/26.12.1+ strongly preferred for security. Previously observed
Dell Node 22.22.1 is unsupported. The embedded ACPX runtime is an
`@openclaw/acpx` plugin, not a required separate `acpx` CLI.
We will not upgrade the Linux host, install OpenClaw, or start a Gateway
without first reviewing the discovery output and choosing the smallest
safe, pinned installation boundary. A dedicated rootless Gateway image
may be preferable to modifying working M0 tooling.

**Next owner command:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/discovery.py
```

Expected output: `M1.0 PREREQUISITES_BLOCKED` or
`M1.0 DISCOVERY_READY`, followed by component version tokens and
`Gateway/ACP/agent execution: NOT_ATTEMPTED`.

## M1 exit gates still pending

Real OpenClaw Gateway → embedded acpx ACP runtime → pinned OpenCode/Ollama
compatibility; read-only planning; canonical exact plan-version/hash
approval; worktree-only coding; ten OS-enforced child-process negative tests;
durable event state and crash/reconnect/cancel reconciliation; independent
Linux and Windows host acceptance. A failing negative test disables
autonomous execution on that host. No M1 completion claims before all gates.

See `docs/IMPLEMENTATION_PLAN.md`, `docs/INTEGRATION.md`,
`docs/SECURITY.md`, and `docs/M1_GATEWAY_DISCOVERY.md`.

**M1.0 owner Dell discovery (2026-11-19):** `PREREQUISITES_BLOCKED` as
expected: host Node `v22.22.1` unsupported; host OpenClaw absent;
host OpenCode `v2.3.2` informational; Podman `5.7.0` available.
No Gateway, ACP or agent execution occurred. M0 remains GREEN.

**M1.1 next:** Prefer separately pinned official image
`ghcr.io/openclaw/openclaw:2026.11.5` in rootless Podman rather than
upgrading installed host Node/OpenClaw. Owner explicitly executes
`python3 tools/m1/gateway_image_probe.py --prepare` to download the
one official image, then validate its immutable identity and patched
Node/OpenClaw versions with fixed no-network, no-mount version probes.
No Gateway or ACP is started and no host packages are installed.
This is an isolated runtime **preflight**, not M1 qualification.
See `docs/M1_GATEWAY_DISCOVERY.md`. M1 remains YELLOW.

**M1.1 Dell qualification (Nov 19): RUNTIME_IMAGE_PASS.** The owner downloaded the official pinned `ghcr.io/openclaw/openclaw:2026.11.5` and verified immutable local image ID `405dcc682726f127d316d435e29d3c5d0cdecb502aef6d74384ce6dd4ffe2700` and registry digest `ghcr.io/openclaw/openclaw@sha256:97781490d6cd01cec8d1f0ad300ff8bedfaae18ec5b8c30666aaebc470a6309f`. The sealed image includes Node `v24.22.1` and OpenClaw `2026.11.5`; host Node/OpenClaw unchanged. No Gateway or ACP ran. Evidence local `artifacts/m1/gateway-image-probe.json`. **M1.1 image/version preflight GREEN; M1 overall YELLOW.**

**M1.2 next:** One *temporary real Gateway* startup on the same immutable ID in rootless, network-disabled, non-root, cap-free, read-only Podman, with NO published ports or host mounts. Create an ephemeral random token; `--allow-unconfigured --bind loopback`. Query only the guest-local `/healthz`, `/startupz`, and `/readyz`, then always stop/remove the container and verify cleanup. No ACP session, model call, project access or stored Gateway profile. Operator: `python3 tools/m1/gateway_loopback_probe.py`. This is an isolated Gateway smoke, **not** ACP integration or M1 security qualification.


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


## M1.6 owner Dell result — real read-only Gateway-to-ACPX RPC PASS

The owner ran `tools/m1/acpx_opencode_rpc_probe.py` on the Dell:
`M1.6 ACPX_OPENCODE_DISCOVERY_PASS`. Live Gateway reported all health
endpoints HTTP 200; the read-only `acpx.agents.list` RPC returned exactly
one OpenCode descriptor `{id:opencode, runtimeId:acp-opencode,
installation:missing, enabled:true}`. The independent pinned ACPX registry
also reported the OpenCode executable **missing**. The disposable Gateway
was verified removed. No ACP session, agent, model or package installer ran.
Local evidence: `artifacts/m1/acpx-opencode-rpc-7c2f4890d37e.json`.

**M1.6 GREEN** means the real authenticated Gateway→ACPX metadata
transport works. It does **not** mean the OpenCode ACP protocol works.

## M1.7 — offline composition of M0-pinned OpenCode and ACPX Gateway

The existing qualified M0.7 toolchain image
`localhost/codingassistant-m0-runtime:2.3.0` already includes
`@opencode/cli@2.3.0`, installed at the global npm binary and package
locations under `/usr/local`. The verified ACPX Gateway image is
`localhost/codingassistant-openclaw-acpx:2026.11.5-e13cf15e`
with immutable image ID
`5aa8b2911525ccf1d5af5ba73ae4bc5f219cdf2a7c91bdf6cfee7208941c5f0c`.

Instead of using ACPX's built-in `npx -y opencode-ai acp` download
fallback, M1.7 adds a dedicated **copy-only** multistage Containerfile
at `infra/m1/Containerfile.opencode-acpx`:
- FROM the exact two **locally inspected immutable image IDs**;
- copy only the global OpenCode 2.3.0 package and launcher into Gateway;
- preserve its Node 24, ACPX, startup and non-root Podman constraints;
- do not copy M0's Node 22, .NET SDK, source trees or secrets;
- `podman build --pull=never --network=none`, with NO Dockerfile RUN,
  package install, internet download, floating base image or image push.

The next owner command (after this PR is GREEN/merged):

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/acpx_opencode_image_probe.py --prepare
```

`--prepare` opt-in **composes a new image entirely from the two
already installed local images**. There are no networked source or
package downloads. Each parent is checked via our previous offline
qualification routines. The new image receives pin and provenance
labels and must have its own immutable local image ID. The runtime
verification, again in sealed `--network=none --pull=never` containers,
requires OpenClaw `2026.11.5`, Node `24.22.1`, OpenCode `2.3.0`,
working **`opencode acp --help`**, and ACPX's read-only native registry
recognizing the installed `opencode acp` executable. A **real, disposable
Gateway** then must report `installation:installed` through the
authenticated `acpx.agents.list` read-only method, with all health
endpoints HTTP 200. No Gateway/agent runtime may publish ports or mount
the host, and temporary Gateway cleanup is mandatory.

If the local images are already composed, omit `--prepare` and the
script only revalidates the image offline. If the older M0 image is
missing or the global OpenCode package launcher is laid out differently,
the build MUST fail closed; do not install OpenCode into the running
Gateway, invoke `npx`, or modify host packages.

Expected potential success:
`M1.7 ACPX_OPENCODE_INSTALLED_DISCOVERY_PASS`.

**This is STILL installation/metadata compatibility, not an ACP
handshake or an authorized coding run.** In particular, native
`opencode acp --help` must exit without initiating an ACP session or
model. Future gates must validate a genuine initialized ACP session,
exact local Ollama model/provider pinning, owner plan/hash approval,
and external process/network/filesystem confinement before source edits.

Official source basis: pinned ACPX v0.21.0 agent registry uses installed
`opencode acp`, and the existing M0 Containerfile pins
`@opencode/cli@2.3.0`. Source references:
- `infra/runtime/Containerfile`
- `https://github.com/openclaw/acpx/blob/v0.21.0/src/agent-registry.ts`
