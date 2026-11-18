# M0.4 — Rootless Linux sandbox preflight

**Purpose:** Verify a constrained disposable container before permitting any agent to execute generated code. This is a preflight, not an authorization for autonomous development, and it does not certify the full M1 security contract.

## Why Podman?

Rootless Podman uses the host user's unprivileged namespaces. With no network, no extra capabilities, a read-only root filesystem, process/memory limits, and a *single disposable workspace mount*, the first seven isolation properties can be checked without granting an agent access to your home or repository. Containers are not a substitute for independent adversarial security testing; M1 still requires all ten mandatory security tests.

Official references:
- https://docs.podman.io/en/stable/markdown/podman-run.1.html
- https://github.com/podman-container-tools/podman/blob/main/docs/tutorials/rootless_tutorial.md
- https://opencode.ai/docs/permissions/

## One-time setup on Dell Kubuntu

**Inspect before installing** (read-only):

```bash
podman --version
```

If absent, these commands update/install system packages (require your sudo password):

```bash
sudo apt update
sudo apt install podman
```

Check that Podman runs **as your normal user, not via sudo**:

```bash
podman info --format '{{.Host.Security.Rootless}}'
```

Expected: `true`. If it fails, stop. Do not disable AppArmor, seccomp, or kernel isolation to make it work.

**Download a disposable Python image intentionally** (this makes network requests and stores an image):

```bash
podman pull docker.io/library/python:3.13-slim
```

On a machine where you need full reproducibility, pin the inspected image digest after first pull. Do not use `--privileged` or mount `/var/run/docker.sock`, your home directory, or the GitHub checkout into a coding container.

## Run the safe negative-test probe

From the project directory:

```bash
cd ~/CodingAssistant
git pull --ff-only
python3 tools/m0/sandbox_probe.py
```

Expected: `M0.4 ROOTLESS ISOLATION PRECHECK_PASS`, then seven `PASS` results. The test runs a fixed Python program in a disposable container. It checks the container is non-root, has no effective capabilities or privilege escalation flag, cannot write the root filesystem, cannot reach public network endpoints, cannot read a deliberately unmounted host file, and **can** write its isolated workspace.

The script creates and deletes its own temporary directories, but does not modify OS configuration, run OpenCode, execute model-generated code, or download container images.

Evidence is written locally to `artifacts/m0/sandbox-preflight.json` and ignored by Git.

### Stop conditions

If any check fails, do not run an autonomous agent. Report the failing check only (do not share secrets). A successful preflight **does not yet allow** OpenCode execution: the full harness needs a tightly scoped Ollama transport, a pinned offline runtime image and separately executed adversarial security tests. Never run `tools/m0/benchmark.py --acknowledge-unisolated-m0-risk` on the Dell host as a substitute.
