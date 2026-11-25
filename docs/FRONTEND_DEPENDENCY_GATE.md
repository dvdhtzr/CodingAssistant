# Frontend dependency gate (#82)

The committed v3 lockfile under `src/CodingAssistant.Web` is the dependency source of truth. All 91 non-root package entries at the baseline have registry.npmjs.org HTTPS tarball URLs and integrity hashes. Direct dependency declarations agree with the lockfile root. Do not regenerate the lockfile during CI or permit implicit dependency upgrades.

CI on Windows and Ubuntu uses `npm ci --no-audit --no-fund`, then `npm audit --omit=dev --audit-level=high` (production graph) and `npm audit --audit-level=high` (including build and test tools). Both are required, not continue-on-error. Audits intentionally consult the registry in the trusted GitHub Actions build only; this does not grant application agents or execution guests network access. A registry failure is a failed gate, not a successful audit. No blanket exceptions or automatic `npm audit fix --force`.

Triage: inspect advisory IDs, affected dependency paths, installed versions, production vs development reachability, and upstream patched versions; prefer a minimal compatible reviewed dependency change with lockfile update. High and critical findings block this CI until remediated; review lower-severity findings separately. Re-run exact-revision frontend and API/browser regression tests before accepting an update. No token handling, remote CDN, runtime policy or model configuration changes.

Acceptance of #82 requires the new CI matrix to pass with the existing lockfile or reviewed fixes. Do not claim an audit result from source inspection alone.
