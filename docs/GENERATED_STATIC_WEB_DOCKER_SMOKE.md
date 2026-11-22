# Owner-invoked generated Static Web ZIP smoke — first real source-specific test

This is a **working but deliberately limited smoke-test lane**, not the
final independent QA executor. It is a pragmatic response to real Windows
M4.7 evidence: 9/11 isolation checks passed, with confirmed container
cleanup, but Chromium's own namespace sandbox crashed even with the
official Playwright seccomp profile.

**Trust boundary**: Chromium runs with `--no-sandbox` **only inside a
disposable, non-root, hardened Docker Linux container**. Docker remains
the isolation boundary. This has less defense in depth than Chromium's
sandbox-on mode. Do not use this path for internet content, unknown
third-party archives, confidential files, or high-risk hostile code.
It does not make untrusted browser isolation fully qualified.

## Real product use

After CodingAssistant's Software Engineer generates a project, download
the existing **Software Engineer source ZIP**. From Windows PowerShell,
with Docker Desktop running, execute:

```powershell
git switch main
git pull --ff-only
node tools/m4/smoke_generated_zip.mjs --zip "C:\path\to\codingassistant-engineering-source.zip"
```

The runner does not require a token, new container image, Linux machine,
manual patches, Azure or API credentials. The owner's explicit `--zip`
selection is required; no-argument mode does nothing.

This is the **first source-specific** browser-test path. The CLI reads a
source ZIP without extracting files to the host. It rejects unsafe paths,
case-insensitive duplicates, symlinks/nonregular entries, directories,
non-Static-Web file types, encrypted/unsupported ZIPs, invalid UTF-8,
corrupt CRCs, Zip64 and archives outside strict file/byte limits.
`index.html` must exist. The ZIP contents and manifest are hashed.

The host streams only the reviewed guest harness plus bounded
base64-encoded project files to the container's stdin. **There are no
host bind mounts, ports, files, credentials or Docker sockets exposed
to the guest.** Docker runs offline, nonroot (1000:1000), read-only
root, capabilities dropped, no-new-privileges, default seccomp,
private IPC, tmpfs, finite CPU/memory/PID/wall limits. Host Docker
context and cached Playwright image are validated before launching.

Inside that disposable container, independent checks require all 9:
exact saved source hashes, nonroot process, no host mounts, denied
system write, denied external TCP, JavaScript syntax, resolved local
asset references (including detection of unlinked app.js), browser
DOM rendering of the user's actual index.html, and source integrity
recheck. Chromium is driven against a localhost-only HTTP server
with an allowlisted path map. Browser crash and unlinked/malformed
source are visible as specific **failing checks**, not fabricated
verification.

Possible results:

- `STATIC WEB SMOKE_PASS_NOT_VERIFIED`: only these smoke checks passed.
  It does not guarantee functional correctness, owner requirements,
  keyboard workflows, history persistence or an independently
  sandboxed browser. It does **not** make the Static Web runtime
  profile QUALIFIED.
- `STATIC WEB SMOKE_TEST_FAILED`: actual saved application content
  failed one or more source/browser checks, with concrete check names
  displayed. The owner may send that failure evidence to the
  Software Engineer; no automated repair is claimed yet.
- `STATIC WEB BLOCKED`: malformed/untrusted archive, invalid Docker
  context, failed containment, guest crash or unconfirmed cleanup.

No source text, browser logs, the original ZIP path or private host
environment is saved to evidence. Only hashes, check flags and bounded
error categories are stored privately under
`%LOCALAPPDATA%\CodingAssistant\qualifications`. The host verifies
the precise labeled container was removed after the run.

## Next PRODUCT milestone, not another qualifying toy

Wire this source-specific test directly into Mission Control through
an owner-approved task/source/plan-bound job (#92), persist evidence
and repair suggestions, let the Software Engineer correct failing
source with at most two iterations, then **rerun independent smoke and
requirements-specific acceptance** (#111). Integrate the ten
adversarial/cancellation/recovery tests (#76) into that actual path.
Never report `VERIFIED` based solely on this smoke result.

M4.7 sandbox-on remains BLOCKED on this Windows Docker host. We do not
silently alter or bypass its existing runtime gate. This standalone
opt-in lane is intentionally separate from the unqualified registry,
current API or background agent executor.
