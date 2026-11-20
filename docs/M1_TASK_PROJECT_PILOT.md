# M1 task-specific project pilot — **not yet product-enabled**

This is a deliberately restricted first integration between the approved
Mission Control interview specification and the **existing**
OpenClaw Gateway → ACPX → OpenCode → locally pinned Qwen coding agent.

The task title, objective, specification, and file plan are dynamic. There is
no C# repair fixture or HTML template in this pilot. However, there is NOT yet
an owner-facing build button wired to this flow. Baseline 1.0 is NOT GREEN.

## Qualifications and security scope

- Linux Dell only, using verified rootless Podman, the pinned combined OpenClaw
  image, an independently pinned offline evaluator image, and local Ollama
  `qwen3.5:4b`. An unavailable image/model must block the run.
- A task-specific contract comes from the authenticated read-only endpoint
  `GET /api/tasks/{id}/agent-contract-preview`. Only the current exact
  **Approved** interview specification with a completed project-file draft can
  be previewed. The preview has `executionEnabled=false` and grants nothing.
- The pilot accepts the UTF-8 JSON bytes of the returned `contract` object,
  saved **owner-private (0600)**. It independently verifies the interview's
  version/hash contract and the source-file allowlist. The pilot currently
  permits a small .NET source project with no other file types or package
  download; this is a deliberately limited toolchain profile, not a
  hard-coded user objective or hard-coded fixture.
- A second full SHA-256 approval covers the contract, model digest,
  immutable coding image, evaluator image, execution script fingerprint,
  and exact file list. No model runs on preview or declined approval.
- The coding agent receives a read-only contract and a fresh empty disposable
  writable project directory, **not an existing repository**. The original
  CodingAssistant repository, `.git`, user secrets, home files and remote
  network are never mounted.
- The guest has no outbound network and is an unprivileged, read-only-root,
  capability-free Podman container with bounded memory/processes. Its only
  inference route is the pinned allowlisted Unix socket bridge.
- Host-side scanning rejects unexpected paths, symlinks, hardlinks, missing or
  oversize files. Only permitted source is exported to a private ZIP.
- Independent .NET restore/build/test tools are run in *separate* rootless
  no-network guests with no inference socket, no host shell, and no
  unreviewed dependency downloads. Actual passing tests must be observed;
  a successful build with **zero tests** is NOT a verified project.
- Results and evidence are saved privately, with actual compiler error codes
  but no raw model transcript, credentials or untrusted source in logs.
- The existing durable M1 run ledger records admission, container launch and
  verification. A terminal result is not automatically retried; interruption
  is not counted as success.

## Qualification usage on the Dell (not ordinary product use)

After PR merge and one-command application update, use Mission Control's
**Download agent pilot contract** button to save the exact approved contract
JSON. The same bytes are available at authenticated
`/api/tasks/{id}/agent-contract-preview/json`. Save the file to a
private JSON file. The pilot also rechecks the live approved specification\nand latest file plan in the local private SQLite database before admission\nand again before accepting a final result. By default it uses the conventional\nKubuntu local application data path; use \`--database /absolute/private/path/codingassistant.db\`\nwhen the launcher has been configured with a different data directory.\n**Do not put any bearer token in filenames, Git or URLs.**
The contract is private because it contains your task and specification.

The owner can then run, from the repository:

```bash
cd ~/CodingAssistant && git pull --ff-only && python3 tools/m1/task_project_coding.py --contract /absolute/private/path/task.json --prepare
```

The optional `--prepare` allows a needed **offline** rebuild of the
already reviewed copy-only coding layer, not a network download. The program
prints the full executable plan hash and requires explicit owner input
`APPROVE <full-hash>`. It starts at most one agent turn. An unavailable
model, container, or source digest fails closed; no cloud fallback.

A source ZIP may be present in
`artifacts/m1/private-task-ledger/task-output-<run-id>.zip`, even when
verification fails. A `SUCCEEDED` ledger state is permitted only if the
model/boundary/source checks pass **and** a separate no-network .NET build
plus actual nonzero test count pass. The code should report incomplete work
truthfully rather than labeling a ZIP a functional application.

### Deliberate limitations and next step

1. This is the first *runtime* pilot, not an integrated Mission Control
   build/retry/preview/export workflow.
2. No dynamic user-specified existing repo mount or Git operations are
   permitted; all agent work is in disposable isolated scope.
3. Only an explicitly selected qualified local model profile is supported.
4. No compiler-diagnostic repair retry has been connected yet. An agent turn
   can fail and the owner can inspect the resulting evidence, but there is
   no automatic success claim or unbounded repetition.
5. The required 10 **integrated** adversarial agent/boundary tests in issue
   #76 and actual positive/negative Dell runs remain mandatory before
   enabling unattended arbitrary project execution. Ubuntu/Windows CI are
   only model-free regression checks.
6. Windows runtime is not qualified; the pilot refuses non-Linux execution.

Next PR should add a secure durable API coordinator for this exact approved
contract, make authorized task build progress visible in Mission Control,
and implement host-driven bounded diagnostics/repair. Keep the original M1.12
fixed-fixture test as a separate Advanced qualification path.
