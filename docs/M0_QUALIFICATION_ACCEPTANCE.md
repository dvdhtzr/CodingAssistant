# M0.15 — independent, offline qualification acceptance

**Scope:** Prepare the actual M0 3×3 benchmark by verifying that **real
generated code** can be checked independently inside the approved OS
boundary. This slice does **not** call Qwen or qualify a model.

## Implementation

- `infra/qualification/Containerfile` extends the existing pinned M0.7
  development image with .NET 10, Node 22 and the React/TypeScript/Vitest
  dependencies from the original third fixture. Dependencies are fetched
  only during **trusted image provisioning**, not during evaluation.
  Package files, the immutable derived image ID and the generated
  lockfile SHA-256 inside the image are part of provenance. The original
  `benchmarks/fixtures/**` and `benchmarks/tasks.json` are unchanged.
- `tools/m0/qualification_acceptance_client.mjs` is a fixed, independently
  authored evaluator, not a model-available tool. It runs from a
  network-disabled, non-root, read-only-root container with a **single**
  writable disposable workspace copy and *no* Ollama socket. It enforces
  non-root/no-new-privileges/zero-capabilities/external-TCP denial,
  no host sentinel or Git mount, and regular input source files.
- For **API feature**, it performs offline .NET restore/build, starts
  the application *inside the same sandbox*, and verifies /health,
  addition, negative integers and HTTP 400 invalid/missing inputs.
- For **C# repair**, it runs offline restore/build and the console DLL,
  comparing output to exact `Total: 15\n`.
- For **full-stack**, it independently verifies the API summary and
  health routes, type-checks the generated React component, then
  injects the **trusted** existing
  `TaskSummary.acceptance.test.tsx` from the immutable image to check
  data rendering and the user-visible network error. Dependencies are
  linked to the immutable read-only image only during evaluation and
  removed afterwards. There is **no npm install** at runtime.
- `tools/m0/qualification_acceptance_probe.py` exports the fixed
  Podman evaluator command and separates environmental/security
  `BLOCKED` conditions from ordinary failed generated code.
- GitHub CI executes all three independent known-good acceptance
  examples and verifies that an intentionally uncorrected C# compiler
  error **fails**. The known-good programs are deterministic CI
  controls, not model-produced data and not nine real coding attempts.

## Gate, risk and follow-on work

The image provisioning command must be an explicit owner operation for
the Dell; never fetch packages while an agent or generated code runs.
A derived image tag is **not a pin**: all runtime Podman invocations must
use its inspected 64-hex immutable local ID and record that ID for each
model trial. Direct package versions are frozen by the original
`package.json`, but the generated dependency lockfile is not yet checked
into source. Reproducibility **across separate image builds** needs
additional review; never silently treat that as complete.

The next cohesive implementation is the **real Qwen/OpenCode 3×3 runner**:
all original prompts, independent fresh copies, exact model/digest,
bounded authenticated host-to-local Ollama broker, tightly scoped native
tools, no generated shell, sealed source/trace hashes, independent
offline acceptance after each attempt and independent safety review.
A model failing compiler or behavior checks must count as **failed
qualification**, not an infrastructure pass or silent repair.

**M0 still YELLOW** until all nine real runs and safety gates pass.
