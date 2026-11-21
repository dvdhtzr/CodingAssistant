# Phase 4A / M4.1 — Fixed-fixture isolated Node syntax check

**Implementation:** development/qualification probe, not a CodingAssistant runtime feature.  
**Scope:** owner-attested Linux rootless Podman only; no generated user source.  
**Initial status:** PENDING REAL HOST ACCEPTANCE. Model-free CI cannot make this GREEN.  
**Related:** [Architecture A1](ARCHITECTURE_EXTENSION_A1.md), [#111](https://github.com/dvdhtzr/CodingAssistant/issues/111), [security #76](https://github.com/dvdhtzr/CodingAssistant/issues/76), [task scope #92](https://github.com/dvdhtzr/CodingAssistant/issues/92).

## Purpose

UX-6B proves that a host-owned scanner can find obvious cross-file defects
in saved generated code without executing it. The next increment must prove
that **a real JavaScript syntax-checking subprocess** can be contained before
allowing it to touch even disposable generated project files.

\`tools/m4/static_web_node_probe.py\` uses the existing immutable owner-pinned
M1 combined image and verified rootless Podman provenance. On a supported
Linux host it runs one disposable **fixed-fixture** Node guest with:
- \`node --check -\` invoked by a child process for syntactically valid
  JavaScript **containing a malicious write side effect**, verifying that the
  syntax checker does not run the side effect;
- \`node --check -\` for syntactically invalid JS, requiring a nonzero status;
- system write, symlink/path traversal, credential read, root escalation,
  missing provider-token, outbound TCP, child-process network/system write,
  and remote Git attempt probes;
- independent host checks of the **exact thirteen** Boolean outcomes;
- explicit container removal and independent absence verification.

The Podman invocation uses the **exact pinned local image ID**, offline
\`--pull=never\`, \`--network=none\`, non-root \`--userns=keep-id\`,
read-only root, no capabilities, no privilege escalation, bounded PID/CPU/
memory, and a single disposable \`/tmp\` tmpfs. It contains **no** host
bind mount, source archive, owner project, \`.git\`, persistent credentials,
published port, container socket, inference socket or remote execution API.

Neither the probe nor the model-free tests make a real browser assertion.
The external TCP and Git observations alone do not establish a full
host firewall or integrated issue #76 negative-security qualification.

## Execution

Review the merged PR and its model-free cross-platform CI first.
On the separately provisioned **Linux Dell** where the pinned M1 image
exists and the original owner-approved rootless Podman boundary is set up:

\`\`\`bash
cd ~/CodingAssistant && git switch main && git pull --ff-only
python3 tools/m4/static_web_node_probe.py --run
\`\`\`

Without \`--run\`, it **does not launch a guest**.

A successful observed run reports \`M4.1 NODE_SYNTAX_FIXTURE_PASS\` with
**13/13** exact guest observations, immutable image provenance and
\`guest_removed=true\`. The full compact, private evidence is saved under
Git-ignored \`artifacts/m4/node-syntax-qualification-*.json\` with mode 0600.
Do not publish full environment logs or credentials.

Any wrong image, package/inventory failure, malformed/false observation,
guest error, platform mismatch, or uncertain cleanup returns **BLOCKED**.
A passing output must come from the real host; a mocked green CI unit test
is only contract logic. No Windows host acceptance is inferred from Linux.

## Deliberate limitations / next gates

A successful M4.1 probe is **not** a Static Web profile qualification and
does not set \`executableViaRegistry=true\`, \`VERIFIED\`, or a
build/test result in Mission Control. The current app endpoint still returns
\`GENERATED_NOT_TESTED\` for UX-6B source preflight.

Remaining dependencies before any task-specific execution:

1. Independently pinned and reviewed offline **Node + Chromium evaluator
   image** with real browser harness, including the frozen #107 Calculation
   Journal negative case and a positive fixture, evaluated after actual
   source generation.
2. Exact owner-approved task/snapshot/file set, runtime image and evaluator
   digests; transient, independently approved worktree mounts; no cloud model
   or unauthorized dependency download.
3. Integrated ten-case attacks against the full M2→M1 execution path (#76):
   symlink/path traversal, subprocess, outbound network/Git and crash/
   cancellation cleanup. The earlier M1.9/M1.10 and this M4.1 probe do not
   replace those integration tests.
4. Real positive/negative qualification on every host architecture:
   Linux first, then separate Windows Docker Desktop/WSL2 acceptance.
5. Only after these, persist actual independent Node/Chromium exit statuses,
   test names/counts, evidence hashes, bounded diagnostics and controlled
   repairs (initial maximum two attempts) for issue #111.

The Source/QA workroom remains functional independently of the executable
runner. This milestone makes isolation behavior more testable without
weakening the secure execution admission model.
