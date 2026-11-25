# Portable image qualification (#70)

## Scope of this slice

M1.1 uses the previously owner-verified OpenClaw GHCR manifest
`ghcr.io/openclaw/openclaw@sha256:97781490d6cd01cec8d1f0ad300ff8bedfaae18ec5b8c30666aaebc470a6309f`
for both local discovery and explicit pull. It requires that exact digest
in the inspection result; another digest from the same repository is
not sufficient. A mutable version tag is display metadata only.
Guests still use the full inspected local image ID, no implicit pull,
and the original rootless/offline restrictions. No subsequent Dell
image ID, evaluator ID, model pin, approval check or execution gate
changes in this slice.

The M1.1 version probe is not an execution qualification. In particular,
the original M1.2 and later probes must still match the previous
owner-approved local IDs. A host that resolves the same manifest to a
different local ID remains blocked by those gates. An engine that cannot
inspect the exact digest also remains blocked; do not fall back to
inspecting a tag.

## Required next slices and evidence

- Inventory immutable upstream base manifests per supported guest
  architecture; bind their identities, the OpenClaw source commit
  `e13cf15ece9bbd969b7362927c315362c90c97ff`, ACPX 0.21.0,
  OpenCode 2.3.0, and reviewed recipe and package/lockfile hashes.
  M0's current runtime Containerfile uses mutable base tags and
  apt/npm provisioning; a version report or matching labels alone
  do not prove a reproducible or independently reviewed build.
- Record full local image IDs and content/manifest digests for the
  official, M0, source-derived ACPX, composed and evaluator images
  in a reviewed host/profile-specific allowlist. Require exact
  matches to independently reviewed inputs and locally inspected
  images. Do not auto-enroll whatever a local tag resolves to.
  Preserve historic Dell pins until a reviewed replacement is
  qualified. Keep candidate evidence separate from authorization.
- Verify immutable IDs again at every execution admission. Bind
  the resulting exact IDs and source fingerprint to the existing
  executable plan and explicit owner approval. An unknown host,
  missing or changed digest, changed recipe, or failed test must
  block; never fall back to a tag, another model or another host's
  pass result.
- Independently exercise positive and negative host qualification
  on Dell and Windows/WSL2, including the mandatory security
  boundaries and disposal/cleanup. Windows model-free CI is
  not evidence of Windows runtime qualification.

## Validation

Deterministic tests cover exact-digest inspection and pull arguments,
rejection of same-repository digest substitution before any guest
starts, malformed digest metadata, multiple digests, and missing images
without implicit download. Run the full Ubuntu/Windows regression CI
before merging. No real container or model qualification is implied
by these tests. Keep #70 open until portable admission and both
independent host qualifications meet its acceptance criteria.

## Slice 2 — separate candidate evaluator provisioning

The historical M0.16 qualification runner is frozen by the independent
M0.18 audit. Keep its bytes, original recipe and original image tag
unchanged; do not update the audit's pinned hashes to silence a failure.
The new opt-in command is:

```sh
python3 tools/m0/evaluator_candidate_provision.py --prepare
```

Without the flag no build or inspection runs. On a normal Linux user
with rootless Podman, explicit provisioning selects the inspected full
local runtime ID, stages only the three fixed recipe inputs in a
temporary context, hashes their exact bytes, and requires a build ID
receipt and matching exact-ID/tag inspection. The candidate recipe has
no mutable base default. Its distinct candidate tag is never consumed
by existing execution paths. No model or generated code is run.

The returned `CANDIDATE_PROVISIONED_NOT_QUALIFIED` evidence is not
authorization, a reproducible dependency build, or host qualification.
The owner must independently review the dependency manifest/lock hashes,
approve the exact content IDs and complete the existing host-specific
positive/negative qualification gates before any new image is admitted.
Do not substitute this candidate into an existing approved execution
plan or reuse historical Dell evidence for it.
