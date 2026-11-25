# CodingAssistant — operator status and release gates

**Checkpoint: November 25, 2026.** This is the concise operational index requested in #71. For historical evidence see [CURRENT_HANDOFF.md](CURRENT_HANDOFF.md), [M0_STATUS.md](M0_STATUS.md), [M1_STATUS.md](M1_STATUS.md), and [SECURITY.md](SECURITY.md). Those histories are preserved. Check exact current GitHub revisions and CI before relying on this checkpoint.

## Release candidate

- Current `main`: `694ebbf5a9a49d562db77a3cd3d23d770aedab10`; [model-free CI green](https://github.com/dvdhtzr/CodingAssistant/actions/runs/45098932538).
- Consolidated [PR #184](https://github.com/dvdhtzr/CodingAssistant/pull/184), source baseline `9c0683bdb7ceacbbcca7652779a0799d29c6f759`, [Windows and Ubuntu green](https://github.com/dvdhtzr/CodingAssistant/actions/runs/45412531756). Includes #178, #179, #180, #181 and #183; each original head is an ancestor. Their PRs are closed as superseded, not merged; no branches deleted.
- Candidate is for owner testing of local interview, approved architecture and plan, source generation, QA/repair, Code Atlas, per-role local models, focused Studio and bounded live activity. Owner live-model/visual acceptance is outstanding; no new generated-execution privilege.
- Separate autonomous-engineering feature branch `746cb05301bdf352094c511ed938f274e807eb53` diverges from #184. Do not merge it: [full regression green](https://github.com/dvdhtzr/CodingAssistant/actions/runs/45603851632), [real trusted sandbox regression red](https://github.com/dvdhtzr/CodingAssistant/actions/runs/45603851530) (security 10/10 and compatibility passed; trusted driver blocked on navigation during backspace/divide-by-zero; cleanup confirmed).

## Evidence boundaries

| Gate | Status | Evidence |
|---|---|---|
| M0 fixed feasibility | Dell-only pass | M0.18, 9/9 frozen functional attempts; see historical status. |
| M1.12 integrated fixed C# repair | Dell-only owner-reported pass | `artifacts/m1/m112-approved-coding-4a73cdebeebf.json` (private file not retrieved); 6/6 local inference, independent fixed-fixture verification. Not arbitrary worktree permission. |
| M2 control plane | Implemented, deterministic CI | Durable approved plans, events, replay, authenticated SignalR; #75 full worker acceptance and #76 remain open. |
| M3 and Phase 2 | Implemented, deterministic CI | UI, source-only generation, versioned stack/style and non-executable profiles; #77 and #109 owner live-model acceptance remain open. |
| Windows trusted sandbox (separate AET branch) | Partial owner-reported evidence | M4.7 11/11 fixed compatibility, private `windows-static-web-5c8b0f40d08c.json`; recent integrated Windows run `windows-static-web-ee0e408516b4.json`: security 10/10, compatibility passed, restart navigation blocked, cleanup confirmed. Private files not independently fetched. |
| General generated-code execution | Not qualified | No new runtime authority; exact scope, independent execution, recovery and adversarial gates remain mandatory. |

## Open issue disposition

| Issue | Remaining acceptance / priority |
|---|---|
| [#76](https://github.com/dvdhtzr/CodingAssistant/issues/76), [#92](https://github.com/dvdhtzr/CodingAssistant/issues/92), [#107](https://github.com/dvdhtzr/CodingAssistant/issues/107), [#111](https://github.com/dvdhtzr/CodingAssistant/issues/111) | Release blockers for independently verified task-driven execution. Keep open; trusted fixtures do not prove autonomous repair of generated code. |
| [#75](https://github.com/dvdhtzr/CodingAssistant/issues/75), [#77](https://github.com/dvdhtzr/CodingAssistant/issues/77), [#109](https://github.com/dvdhtzr/CodingAssistant/issues/109) | Much implemented on main; existing issue acceptance comments explicitly retain live worker, Dell browser or owner local-model gates. Do not close on model-free CI alone. |
| [#110](https://github.com/dvdhtzr/CodingAssistant/issues/110) | Portable skill registry and audited activation not implemented; role skill metadata is not a loader. |
| [#86](https://github.com/dvdhtzr/CodingAssistant/issues/86) | Alternate Dell model qualification remains distinct from Windows per-role model settings. |
| [#82](https://github.com/dvdhtzr/CodingAssistant/issues/82) | Lockfile present; CI still uses npm install without audit. Complete deterministic install and vulnerability triage. |
| [#68](https://github.com/dvdhtzr/CodingAssistant/issues/68), [#69](https://github.com/dvdhtzr/CodingAssistant/issues/69), [#70](https://github.com/dvdhtzr/CodingAssistant/issues/70), [#72](https://github.com/dvdhtzr/CodingAssistant/issues/72) | Recovery-evidence preservation, ledger crash consistency/provenance, image portability, retention/privacy. Keep open until acceptance tests pass; #68 blocks optional real orphan probe, #72 blocks non-disposable repo access/sharing. |
| [#71](https://github.com/dvdhtzr/CodingAssistant/issues/71) | This canonical summary supplies the requested status index. Close after reviewed integration to main; retain historical records. |

## Next action

Review and authorize the consolidated candidate independently of AET. Confirm CI on its final revision; then owner can exercise source-only workflows and real local Ollama using main after approved merge. No general execution or verified-delivery claim. Continue the separate trusted driver repair and mandatory security gates before any wider runtime admission.
