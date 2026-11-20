# M1 secure orchestration — status

**Status (November 19, 2026): YELLOW / Gateway-owned ACPX diagnostic GREEN on Dell; full coding integration NOT YET QUALIFIED.** M1.11's corrected registered Gateway RPC passed real Dell runtime acceptance after PR #56; see last section and `docs/CURRENT_HANDOFF.md`. Earlier sections preserve historical failures.

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

## M1.7 Dell real outcome — November 19, 2026

**M1.7 ACPX_OPENCODE_INSTALLED_DISCOVERY_PASS.** The owner ran
`tools/m1/acpx_opencode_image_probe.py --prepare`, which composed the
already-qualified M0 OpenCode image and M1.4 ACPX Gateway image entirely
offline. Parent M0 image:
`3731c286c18c8ea8befd886a24016e197a084f391e3c1c4d95c2d86421502a81`.
New derived immutable image:
`023bf9411fe23045dbdea9c777ed8618dec6553a81417c9aec2d15f7cf5b99ea`.

Offline guest checks confirmed Node `v24.22.1`, OpenClaw
`2026.11.5`, OpenCode `v2.3.0`, `opencode acp --help`, and ACPX
registry `installation:installed`. The actual no-network Gateway
returned HTTP 200 health/readiness and its authenticated
`acpx.agents.list` method independently reported OpenCode
`installation:installed`. The disposable Gateway was removed.
No ACP session, model call, source modification or external network
access occurred. Local evidence:
`artifacts/m1/acpx-opencode-installed-1642a6de841b.json`.
**M1.7 GREEN; overall M1 remains YELLOW.**

## M1.8 — real ACPX-to-OpenCode ACP initialize, no coding session

The pinned upstream ACPX `v0.21.0` runtime's `doctor()` calls its
actual `probeRuntime`, which creates an ACP client, starts the pinned
adapter, sends the ACP `initialize` request, records the negotiated
protocol version, then **closes the process**. It does **not** request
`session/new` or `session/prompt` (source:
`openclaw/acpx@v0.21.0/src/runtime/public/probe.ts`).
This is not a synthetic protocol mock.

The new one-shot probe
`tools/m1/acpx_initialize_probe.py` verifies the exact local
M1.7 immutable image ID, parent provenance, OpenCode version, and
installed adapter registry; then launches the ACPX official runtime
inside a disposable rootless, no-network, no-mount, no-ports,
read-only guest with strict memory/PID/CPU budgets. The ACP runtime
is configured `permissionMode:deny-all`, `nonInteractivePermissions:deny`,
`fs:false`, `terminal:false`, no MCP servers, and no inherited
AI/provider credentials. Its `onBeforeSpawn` process admission hook
permits exactly one executable and argument pair:
`/usr/local/bin/opencode acp` in the runtime-probe scope. Any
unexpected process or new session request is out of scope.

For PASS, the real ACPX `doctor` must confirm
`embedded ACP runtime ready`, a bounded negotiated
`protocolVersion`, exactly one admitted/spawned child, successful
runtime shutdown, and verified removal of the entire temporary
Podman guest. No Ollama connection, cloud model, prompt, ACP
coding session, actual worktree, or original source file is used.
Raw CLI output, trace content and credentials are NOT persisted;
only bounded status fields are saved in append-only ignored
`artifacts/m1/acpx-initialize-*.json`.

**Next Dell command after this PR GREEN and merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/acpx_initialize_probe.py
```

Potential success: `M1.8 ACPX_OPENCODE_INITIALIZE_PASS`.
If BLOCKED, keep M1 YELLOW and examine the saved reason before
changing adapter, container or authorization settings.

**Important scope:** This confirms ACPX ↔ OpenCode initialization
in a sealed process, **not yet a Gateway-delegated coding session**.
The next gated work is true Gateway-owned ACP orchestration,
read-only plan generation with exact owner approval version/hash,
enforced external child-process sandboxing, ten negative security
cases, durable recovery and independent Windows-host acceptance.



## M1.8 — owner Dell ACP initialize verification (GREEN, November 19, 2026)

The Dell ran `python3 tools/m1/acpx_initialize_probe.py` against the
pinned combined image ID
`023bf9411fe23045dbdea9c777ed8618dec6553a81417c9aec2d15f7cf5b99ea`
and received **`M1.8 ACPX_OPENCODE_INITIALIZE_PASS`**.
The *real* `acpx@0.21.0` runtime and OpenCode 2.3.0 completed ACP
`initialize`, negotiated `protocol_version: 1`, admitted/spawned
**exactly one** approved `opencode acp` child, shut down the runtime
and removed the guest. No `session/new`, `session/prompt`, model,
network or project file operation occurred. Evidence:
`artifacts/m1/acpx-initialize-33e62303215e.json`.
This is a protocol handshake test, **not Gateway-owned delegation**.

## M1.9 — OS-enforced adversarial boundary qualification (next)

Before enabling an actual Gateway-owned coding session, confirm that
our exact combined image's rootless OS boundary rejects hostile
operations, **including operations launched by child processes**.
The new `tools/m1/os_negative_boundary_probe.py` runs twelve
independent negative cases inside ONE temporary rootless/no-network,
no-mount/no-published-port, no-capability, non-root, read-only-root
Podman guest. CPU/memory/PID budgets and one disposable `/tmp`
tmpfs apply. Tested attempted violations:

1. Writing an OS config file in `/etc`.
2. Symlink-based write escape into `/etc/passwd`.
3. `..` traversal write into `/etc`.
4. Reading `/etc/shadow`.
5. Reading shadow credentials through `/proc/1/root`.
6. Accessing the owner's unmounted CodingAssistant host worktree path.
7. Inheriting sensitive provider/GitHub/Ollama credentials.
8. Changing the executing process to root.
9. Opening an outbound TCP connection to documentation-only
   `203.0.113.1:443` despite `--network=none`.
10. Reaching the *host* Ollama via the container's
    `127.0.0.1:11434` (must not be reachable).
11. Bypassing network restrictions from an independently spawned
    child Node process.
12. Modifying `/etc` from an independently spawned child Node process.

Passing observations MUST show each case was denied or its protected
target was unavailable. One exposure, malformed observation,
unexpected guest exit, wrong image, or failed container cleanup
returns **BLOCKED**, and autonomous execution remains disabled.
Only bounded boolean observations are saved in append-only ignored
`artifacts/m1/os-negative-audit-*.json`, not raw error logs.
No Gateway, ACP session, OpenCode coding task, model, inference
or host repository is executed/modified. This probe does not add
a writable project mount, network route, or permission override.

**Run on Dell after the corresponding PR is GREEN and merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/os_negative_boundary_probe.py
```

Expected success: `M1.9 NEGATIVE_SECURITY_PASS` and **12/12**
individually denied. The tests are a **bounded, image-specific
security layer**; they do NOT prove that future projects,
symlink policies for approved worktrees, Git remote permissions,
Gateway-to-ACP session/plan approval workflows, Ollama socket
boundaries, or Windows-host controls are safe. All still require
targeted tests and explicit authorization before autonomous coding.
Overall M1 remains **YELLOW**.



## M1.9 owner Dell result — GREEN (Nov 19, 2026)

Owner ran `tools/m1/os_negative_boundary_probe.py` against the
exact combined local image
`023bf9411fe23045dbdea9c777ed8618dec6553a81417c9aec2d15f7cf5b99ea`.
Result **`M1.9 NEGATIVE_SECURITY_PASS`**, **12/12** denied:
system/symlink/traversal writes, shadow and /proc credentials,
unmounted user repository, inherited provider secrets, privilege
escalation, outbound TCP/host Ollama loopback and independent child
process network/system writes. Disposable container removed. This is a
**bounded image-specific OS boundary check** (even a failed public
outbound TCP connection is not a full network-policy proof). No
Gateway, ACP session, model, source modification or cloud API ran.
Local evidence: `artifacts/m1/os-negative-audit-a80a37fa58d7.json`.

## M1.10 — writable disposable project boundary, not a real repo

To validate the next critical boundary **before** any real writable
worktree is admitted, the new `tools/m1/scoped_worktree_probe.py`
generates a fresh throwaway miniature C# project under Git-ignored
`artifacts/m1/ca-m110-*/approved` and an adjacent UNMOUNTED
`unapproved/keep.txt` sentinel. The only host bind is the
newly-created `approved` directory to `/workspace`; the actual
CodingAssistant repo, host home, unrelated repos, any `.git` tree,
credentials and external API sockets are NEVER mounted.

A fixed Node program running inside the already-verified immutable
OpenClaw/ACPX/OpenCode image, in rootless, non-root Podman with
`--network=none --read-only --cap-drop=all`, verifies that:

- Approved edits to `App.cs` and newly-created `Feature.cs` succeed.
- A child process may write a NEW authorized `Child.cs` in the same
  project scope.
- Symlinks to `/etc/passwd`, path traversal reads/writes to the
  unmounted sibling, system writes, and child process writes outside
  the project are denied.
- User's real project is not visible and `.git` is not mounted.

The trusted Python parent then INDEPENDENTLY hashes the unmounted
sibling sentinel and checks the exact modified/new source file bytes,
unchanged fixture notes, exact file-set, symlink target and absence of
Git metadata. The disposable container and temporary fixture must
both be removed and their cleanup verified before reporting PASS.
This avoids trusting claims made solely by the guest program.
There is no agent, ACP session, Ollama, hosted AI service or Git remote.

**Run on Dell after PR GREEN and merge:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/scoped_worktree_probe.py
```

Expected: `M1.10 SCOPED_WORKTREE_PASS`, **10/10** guest
observations, **9/9** independently confirmed host-side checks, and
both removals confirmed.

**Scope boundary:** This proves a disposable single-project bind mount
can be writable while neighboring host files are inaccessible. It
does not authorize a real repo mount, untrusted generated code,
auto-updated dependencies, Git remotes, Gateway-owned ACP coding
turns, or model connectivity. Product M1 still needs exact
plan-version/hash approval, model network segregation, durable run
state/crash recovery, and independent Windows acceptance. M1 stays
**YELLOW** until those gates pass.


## M1.11 — authenticated Gateway-owned ACPX/OpenCode doctor (no coding session)

M1.9 (12/12 negative OS tests) is verified GREEN on the Dell.
M1.10 (one writable disposable project with independent sibling
integrity audit) was merged as PR #52; owner Dell runtime acceptance
remains independently gated. The doctor test does not rely on or
mount that writable project.

Review of pinned OpenClaw `v2026.11.5` code confirms built-in
`/acp doctor` dispatches to the **Gateway's registered ACPX runtime**
`doctor()`, which initializes the installed OpenCode ACP server, then
closes it. It does not request an ACP coding session/prompt. This is
different from M1.8's standalone doctor and M1.6's metadata-only RPC.

The M1.11 probe launches one disposable Gateway with the exact M1.7
combined immutable image ID. It uses rootless non-root Podman,
`--network=none`, read-only root, no host mounts or published ports,
and one-time owner-only token. After all health endpoints report 200,
it sends exactly this fixed **Gateway-backed**, not `--local`,
CLI command:

```bash
openclaw agent --session-key agent:main:ca-m111-doctor-<nonce> --message '/acp doctor' --timeout 45 --json
```

An ordinary OpenClaw agent CLI command may create temporary
Gateway conversation bookkeeping within isolated `/tmp`; this
does not imply an ACP coding session. No provider endpoint or
host Ollama is reachable, no project mount or provider key exists,
and no autonomous code modification is requested. The test
accepts only a genuine structured doctor result containing
`ACP doctor:`, `registeredBackend: acpx`,
`runtimeDoctor: ok (embedded ACP runtime ready)`,
`runtimeDoctorDetail: protocolVersion=1`, and zero active ACP
sessions and turns. A misrouted request, model-generated prose,
invalid response, nonzero exit or cleanup failure blocks PASS.
Only sanitized protocol/health metadata is recorded; raw Gateway
output, logs, tokens and session identifiers are discarded.

**Next Dell command after M1.11 PR GREEN/merged:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_acp_doctor_probe.py
```

Potential result: `M1.11 GATEWAY_OWNED_ACP_DOCTOR_PASS`.
Even success establishes only the Gateway-owned diagnostic path.
Actual coding still requires exact plan/version/hash owner approval,
scoped writable worktree gates, local-only inference route, Git
restrictions, recovery and independent Windows-host runtime tests.
Overall M1 remains YELLOW.


## M1.11 owner Dell outcome — BLOCKED; sanitized exit diagnosis

Owner ran `tools/m1/gateway_acp_doctor_probe.py` after PR #54 merge.
The exact pinned composed image and adapter preflight passed, Gateway
health endpoints `/healthz`, `/startupz`, `/readyz` were all HTTP 200,
and the temporary Gateway was verified removed. The fixed Gateway-backed
`/acp doctor` CLI exited **nonzero**, so no Gateway-owned ACP doctor
response was accepted; **M1.11 remains BLOCKED**. The known outcome
does not invalidate M1.8 standalone ACP initialization or M1.10
disposable scoped-worktree verification.

Local ignored failure evidence:
`artifacts/m1/gateway-acp-doctor-2b64833ae071.json`.

The earlier script intentionally discarded all CLI stdout/stderr,
leaving us unable to distinguish CLI parsing, auth/pairing, Gateway
request admission, provider/model configuration, or runtime failure.
**No workaround to credentials, model inference, network, or host mounts
is permitted before diagnosing this exact path.**

This PR adds a bounded, fixed-label `doctor_failure_diagnostic` on
nonzero exit: status code, output shape, known error family, and
presence-only markers. The classifier emits NO raw strings, paths,
device IDs, tokens, stderr, CLI output, user text, or freeform
external error values. Unknown error returns `unclassified`.
Nonzero results always remain BLOCKED; cleanup is still mandatory.
New mocked tests deliberately inject secrets and assert no leaks.

**After PR is GREEN and merged, rerun exact safe M1.11 command:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_acp_doctor_probe.py
```

The next result may still be BLOCKED. Use its *sanitized* category
to decide a narrowly scoped upstream-compatible fix. Do not paste
raw OpenClaw or Podman logs. M1 overall remains YELLOW.

## M1.11 corrected Gateway-owned ACPX doctor (CLI slash command retired)

Owner Dell result after PR #55: M1.11 remains **BLOCKED** on the original
`openclaw agent --message '/acp doctor'` invocation. Combined immutable
image and package preflight passed; all 3 Gateway health endpoints HTTP 200;
Gateway cleanup PASS. CLI exited 1, returned a JSON object, and stderr,
but sanitizer reported `category: unclassified` and no ACP doctor marker.
Evidence: `artifacts/m1/gateway-acp-doctor-a3289d889e62.json`.

**Source finding:** pinned OpenClaw v2026.11.5
`src/commands/agent-via-gateway.ts` documents that CLI-originated
slash commands can be rejected and fall through to normal agent turns
(it explicitly rejects `/compact` for that reason). Sending `/acp doctor`
through `openclaw agent --message` is therefore not a safe/proven
Gateway diagnostic transport. We do not claim this proves the precise
cause of the owner's unclassified CLI failure; we retire that route.

The replacement registers an explicit `codingassistant.acp.doctor`
Gateway method via a tiny **temporary diagnostic plugin**, using the
pinned official plugin API `openclaw/plugin-sdk/acp-runtime-backend`.
The method accepts exactly `{}`, requires `operator.read`, obtains
the **existing Gateway-registered ACPX backend** via
`getAcpRuntimeBackend('acpx')`, calls its real `runtime.doctor()`
and returns only a bounded, fixed schema containing ACP protocol 1
and no coding-session/model claims. Unexpected params, missing
backend, invalid protocol or error cannot return a success response.

**Build/run only on Dell, after PR CI GREEN and merge:**

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/gateway_backend_rpc_probe.py --prepare
```

`--prepare` explicitly creates a **new, disposable-purpose derived
local image** from immutable M1.7 combined image
`023bf9411fe23045dbdea9c777ed8618dec6553a81417c9aec2d15f7cf5b99ea`.
Podman build is copy-only (`FROM` pinned local ID, no `RUN` or npm),
`--network=none --pull=never`; original M0/M1 images stay unchanged.
The two added files copied into the image are an explicit operator-only
plugin under `/app/codingassistant-doctor` and an immutable no-secrets
OpenClaw config under `/app/codingassistant-doctor-config.json`.
The config activates ACPX and the diagnostic plugin, pins
`/usr/local/bin/opencode acp`, probe agent `opencode`, denied
interactive permissions, no MCP server, and empty agent env allowlist.
Gateway runtime runs rootless/non-root, no-network, no host mounts or
ports, read-only root and private tmpfs/token. It calls exactly the
registered `gateway call codingassistant.acp.doctor --params '{}' --json`
RPC—**never** a general agent CLI or user prompt.

Passing requires the actual Gateway to return only the schema
`backend=acpx`, `runtimeDoctorOk=true`, `protocolVersion=1`,
`codingSessionCreated=false`, `promptSent=false`, and verified removal
of the disposable Gateway. Appendix records only image digest, source
fingerprint and bounded success flags. No provider/model inference,
ACP coding session, source workspace access, remote network or Cloud API.

Potential PASS: `M1.11 GATEWAY_OWNED_ACP_DOCTOR_PASS`.
A BLOCKED result remains the truth; do not relax config, network or
provider policy to force success. This diagnostic plugin is **not**
part of a production coding runtime and does not authorize code edits.
Owner plan hash approvals, source Git worktree permissions, local-only
networked Ollama transport, durable crash recovery and Windows host
runtime acceptance remain future M1 exit gates.


## M1.12 — first approved disposable Gateway → ACPX → OpenCode → local Qwen coding turn

**Status: implementation proposed; real Dell runtime acceptance NOT YET RUN.**
M1.11 Gateway-owned diagnostic passed on the owner Dell: all Gateway health endpoints
200, actual ACPX backend protocol 1, disposable container removed. Local ignored
evidence `artifacts/m1/gateway-backend-rpc-a1c64f120648.json` and exact
derived diagnostic image `61f1a21dcdbe2f70181b8788641bee0f7fc9b249290ecacedeb793244e8d5eb6`.
That result did **not** start an ACP coding session or call a model.

This cohesive M1.12 implementation deliberately reuses the original
`02-build-repair` frozen C# task rather than admitting a real repository.
The new opt-in `tools/m1/approved_gateway_coding.py`:
- Requires Dell rootless Podman, immutable combined OpenClaw/ACPX/OpenCode image
  `023bf9411fe23045dbdea9c777ed8618dec6553a81417c9aec2d15f7cf5b99ea`,
  exact local Qwen model digest, original fixture/manifest hashes, and an
  independent no-network evaluator image.
- Builds an **offline copy-only** pinned source layer with a one-use
  `operator.write` Gateway method. Only fixed task `api/Program.cs` may be
  changed. Gateway method invokes the actual registered ACPX runtime
  `ensureSession` and `startTurn`, not a CLI slash command or local mock.
- Shows the **full versioned plan SHA256**, then requires typing exactly
  `APPROVE <64-character plan hash>` before any coding container is started.
  Refusal/EOF produces NOT_APPROVED and no coding session.
- Runs no-network, rootless, non-root, read-only-root, cap-free Podman with only
  a newly copied writable fixture directory and a private read-only-mounted
  AF_UNIX broker. A guest-local loopback relay exposes only fixed Ollama API
  routes; the host broker validates the model/digest and bounded real streaming
  responses. No cloud keys, personal repository, network route or Git remote.
- Requires the real Gateway/ACP turn to complete, **at least one verified
  pinned-Qwen broker inference**, an authorized Program.cs change, an unchanged
  project file and adjacent unmounted sentinel, verified container removal,
  and a **separate offline evaluator PASS** before reporting
  `M1.12 GATEWAY_APPROVED_CODING_PASS`.

**Dell command only after reviewed PR merged and model-free CI GREEN:**

```bash
cd ~/CodingAssistant && git switch main && git pull --ff-only && python3 tools/m1/approved_gateway_coding.py --prepare
```

Review the printed exact plan hash and type the full `APPROVE <hash>` only
if you approve this fixed **disposable** coding task. This is an actual local
model invocation and source edit to a temporary fixture; it may be slow. If
the run reports BLOCKED, report only its bounded status/flags; do not paste
raw Gateway/Ollama logs or weaken the container or approval. CI tests here
are model-free and cannot substitute for owner Dell runtime evidence.

Passing this one fixture is **not full M1 qualification**: durable run-state,
crash/restart reconciliation, broader approval scope and Windows runtime
security remain separate mandatory gates.


## M1.12 — Dell owner-approved run BLOCKED; sanitized failure evidence corrected (November 19)

The first **owner-approved** disposable C# coding run passed full-hash
authorization `ed4d0a5f90306b8f6feb3eb059770709c0f674aa855077153ed2670d980e4b2a`,
but returned **`M1.12 BLOCKED`** with `reason_code: ValueError`,
`gateway: None`, `broker: None`, `evaluator: None`. The prior runner
discarded the actual phase and partial evidence when an exception escaped
`run_gateway`. It is **not possible** to infer from those fields whether
Gateway started, whether Qwen was called, or exactly what failed. Do not
claim model inference or independent build acceptance.

A follow-up single-slice fix preserves sanitized `phase` plus the
Gateway launch/readiness/ACP stage, container cleanup result, independent
model broker's bounded attempt/verified/denied counters, and a status-only
`operator.read` response from the *same* approved Gateway plugin. All
fields are allowlisted enums/booleans/integers; **no raw stdout/stderr,
prompts, source bytes, env values, tokens or model responses** are retained.
Full plan-hash owner approval is still mandatory, and no new standalone
diagnostic/test assignment is authorized.

A reattempt is justified only after model-free CI GREEN and this PR merges:

```bash
cd ~/CodingAssistant && git switch main && git pull --ff-only && python3 tools/m1/approved_gateway_coding.py --prepare
```

The owner must review and paste the **fresh entire** `APPROVE <hash>`
line for the same fixed disposable repair. Report `M1.12` status, `phase`,
`reason_code`, Gateway flags/stage and broker counts. Neither CI nor a
failed attempt may mark M1.12 GREEN.


## M1.12 — Gateway coding RPC rejected before inference: startup activation omission

On the second owner-approved Dell run, the Gateway passed `/healthz`,
`/startupz`, and `/readyz` (all HTTP 200), and its disposable container
was removed. The coding RPC returned nonzero, the plugin's read-only status
RPC was unavailable, and the host model broker recorded **zero attempts**.
The independent evaluator was not reached. **M1.12 remains BLOCKED.**

Root cause identified by comparing M1.12's manifest against the
owner-qualified M1.11 diagnostic plugin and pinned OpenClaw v2026.11.5
startup activation contract: `infra/m1/coding-plugin/openclaw.plugin.json`
omitted `activation.onStartup`. In the pinned version, merely enabling
a manifest-bearing plugin in config does not cause it to be imported at
Gateway startup without an activation trigger; consequently its registered
RPC methods were absent. The corrected manifest declares
`"activation": {"onStartup": true}`. Model-free CI tests now enforce that
manifest contract.

This change does not relax model selection, user approval, Podman isolation,
or workspace permissions. **Runtime behavior after this correction is not
yet proven**; an exact fresh owner-approved Dell disposable coding run must
pass before M1.12 can be marked GREEN.


## M1.12 — repeatable ACPX session initialization failure (November 19)

Two owner-approved identical real Dell attempts at commit `bb47a44`
returned `CODING_RPC_NONZERO` while Gateway health was **3/3 200**,
`claimed=true`, plugin phase `failed` and
`failureAt=session_initialize`; both disposed of their Gateway
containers. The host-local model broker recorded **zero attempts**.
Neither the ACP prompt nor the independent .NET evaluator was reached.
This narrows the failure to **ACPX `ensureSession`**, which covers
OpenCode agent startup, `session/new`/catalog loading and exact model
selection. It does **not** identify which operation failed.

The pinned ACPX 0.21.0 source uses typed process, timeout and model
rejection errors, but M1.12 previously discarded the error object
inside its Gateway plugin. The present fix introduces a bounded
`lastFailure` response with only fixed enum values (model unsupported,
catalog empty, agent exit/spawn, file denied/missing, timeout, generic
session initialization), a numeric process exit code (if known), and
optional fixed model-reason token. Raw exception messages,
provider responses, stderr, source paths, prompts and credentials
are **never** returned or saved. The exact source/plan hash changes,
so a fresh user approval is required for the next real disposable run.

No automatic fallback model or unapproved `ensureSession` retry was
introduced. If the next owner-approved attempt fails, the combined
bounded plugin stage + typed error is the acceptance evidence to use
for a *specific* root-cause correction. A CI-green classifier is
only a diagnostic regression gate, not an M1.12 PASS.


## M1.12 — actual approved Dell result: OpenCode agent startup failed

At main commit `3b7262f`, the owner approved the fixed disposable C#
repair plan. The registered coding Gateway plugin was invoked and
failed at `ensureSession` with a typed
`lastFailure.kind=AGENT_STARTUP_FAILED`,
`errorCode=AGENT_STARTUP_FAILED`, `exitCode=null`.
Gateway health was 3/3 HTTP 200; Podman removed the container; the
host Ollama broker saw **zero requests**. No agent prompt, model
inference, source repair or evaluator passed. M1.12 remains **BLOCKED**.

Review of the **actual** M0 successful OpenCode launch environment
found its child was given explicit XDG data/state/cache paths under
the isolated writable `/tmp` tmpfs. M1.12's Gateway launch inherited
`HOME=/tmp` and `XDG_CONFIG_HOME=/tmp/config`, but did **not**
provide those other three explicit XDG paths. This is a confirmed
environmental difference, **not** a confirmed causal explanation for
the startup failure. The follow-up makes all three explicit using the
already-mounted disposable `/tmp` tmpfs, preserving all containment
and model/provider restrictions.

Pinned ACPX 0.21.0 constructs `AgentStartupError` with a possibly
null `exitCode` and a separate OS signal or nested spawn error.
Previous diagnostics dropped both. The read-only exact-plan status
now exposes *only allowlisted* `signal` and `systemCode` (or null)
alongside the existing enum fields; raw stderr, messages, stacks,
source paths and environment are still excluded. If a future
owner-approved attempt is blocked, use those typed observations
before changing quotas or model configuration.

GitHub's Ubuntu/Windows model-free checks do not prove the Dell's
real ACPX startup or C# repair. User approval of each fresh plan
remains mandatory.


## M1.12 — SIGABRT on Dell; bounded combined PID allowance

On the real owner-approved Dell run at `3fe8c9e`, the Gateway
passed all three health endpoints and invoked the coding method, but
ACPX reported `failureAt=session_initialize`,
`lastFailure.kind=AGENT_STARTUP_FAILED`,
`lastFailure.signal=SIGABRT`, `exitCode=null` and no typed OS
error. The inference broker recorded **zero requests**; independent
C# evaluation never started. The container was removed safely.
The previous XDG-directory adjustment did **not** resolve startup.

Review of the repository's actual Podman options found that M1.12
launches **two Node runtimes (relay and Gateway) plus Bun/OpenCode**
within `--pids-limit=96`. M1.8's successful standalone ACP initialize
used the same 96-task cap but **only its single probe process plus
OpenCode**, under `--cpus=2`. The pinned Bun/OpenCode runtime has a
documented `SIGABRT` failure mode on thread creation exhaustion (for
example Bun issue `oven-sh/bun#44787`). This supports a **specific
PID-pressure hypothesis**, **not** proof that the observed abort was
caused by this cap.

The combined-session fix increases the still-enforced container
task/PID limit to **256**, adds the previously qualified CPU quota
`--cpus=2`, and keeps `--memory=3g`, rootless, `--network=none`,
read-only root, fixed workspace access, and private local-model relay
unchanged. Before disposable cleanup, it attempts a read-only query
of kernel cgroup `pids.max`, `pids.peak`, and
`pids.events.max`. The evidence parser accepts **only three bounded
nonnegative integers**, and discards missing/errorful/raw output.
`max_events>0` would prove saturation was reached **during that
container's lifetime**, while zero or unavailable values cannot
exclude all other thread-failure causes. No system logs or credentials
are exposed.

A fresh exact owner approval is required because the source
fingerprint and derived immutable image change. **M1.12 remains
BLOCKED until live local-model activity and independently evaluated
C# repair actually pass.** GitHub model-free CI is not that proof.


## M1.12 — owner-approved Dell integrated coding GREEN (November 19, 2026)

**Status: `GATEWAY_APPROVED_CODING_PASS` on Dell.** This later
owner-observed acceptance supersedes the historical M1.12 BLOCKED
attempts retained above. Execution used GitHub main
`191a7b0ef9e99133fcdc535abad2236265f8c029` and the explicitly
approved exact plan SHA256
`0349c4327de288058ca556289e09839235db3e84bc699e06b1fbd0582dc5cd06`
for the frozen disposable `02-build-repair` C# fixture and pinned
`qwen3.5:4b`. The owner reported local ignored evidence:
`artifacts/m1/m112-approved-coding-4a73cdebeebf.json`.

The real isolated OpenClaw Gateway passed all three health probes
(`/healthz`, `/startupz`, `/readyz`: **200/200/200**). Its registered
coding plugin delegated a real session to ACPX/OpenCode, sent the
approved prompt, and completed the coding turn. **17 ACP events** and
**9 tool calls** were observed. The host-only, digest-rechecking
local Ollama broker recorded **6 attempts / 6 verified / 0 failed /
0 denied**. The separate offline .NET evaluator returned
`{"status":"VERIFIED","verified":true}`. The isolated coding
container was removed. No cloud route, real owner repository, or
Git remote was enabled for this task. The runner therefore returned
`M1.12 GATEWAY_APPROVED_CODING_PASS` with `phase=complete`.
This is real end-to-end acceptance, **not a model-free CI assertion**.

The combined Gateway/Node/OpenCode container used an enforced
**256-task cgroup ceiling and two-CPU quota**; kernel counters
reported `pids.max=256`, `pids.peak=107`, and
`pids.events.max=0`. The observed peak **exceeded the old 96-task
configuration**, strongly supporting the hypothesis that the prior
tight cap contributed to repeated Bun/OpenCode `SIGABRT` failures;
the previous failed runs did not retain equivalent peak data, so
their exact causes are not mathematically proven.

**Boundary:** M1.12 GREEN validates one fixed, reviewed,
exact-plan-approved disposable C# coding task **on the Dell only**.
M1 as a whole stays **YELLOW**: durable execution history,
cancel/pause/reconnect/crash reconciliation, idempotency, integrated
hostile-operation denial tests, general worktree policies, and
Windows-specific qualification remain to be implemented or accepted.
Do not authorize arbitrary personal repositories or describe this
as a production-ready autonomous coding product.

**Next work:** follow `docs/IMPLEMENTATION_PLAN.md` and
`docs/CURRENT_HANDOFF.md`; prioritize cohesive M1 durable-state/
reconciliation/negative-test functionality in an operator-visible
vertical slice, then the M2 .NET 10 + SQLite foundation and M3
React/SignalR Mission Control. Do not repeat the already-GREEN
M1.12 coding test without a specific regression or scope change.

