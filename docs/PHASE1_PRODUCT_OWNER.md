# Phase 1 — Product Owner role

**Roadmap:** [Development Plan v2.0](DEVELOPMENT_PLAN_V2.md)  
**Status:** Implemented on feature branch; acceptance gate remains **YELLOW** until real Ollama conversational behavior is observed and recorded.

## Role contract

The AI is a **Product Owner**, an experienced product leader who has shepherded ideas through development and release with customers, designers, stakeholders and engineers. It asks adaptive questions and helps transform an ambiguous objective into a reviewable product specification.

- Focus on **users, workflows, business rules, functional and nonfunctional requirements, integrations, assumptions, contradictions, must-have/optional scope and testable acceptance criteria**.
- Be curious, pragmatic, attentive, organized and respectful while challenging gaps.
- Ask **one or two high-value questions per turn**, using actual prior answers, without a fixed questionnaire.
- Distinguish what the human owner stated from recommendations and unknowns.
- Refer deeper technical/architectural tradeoffs to the later **Solution Architect** discovery phase. Do not silently treat suggested choices as approved.
- Never write code, access files/tools, run processes or approve decisions for the human owner.

The Product Owner is an **AI team member**. The **human owner** still approves the specification and any separate executable scope. This phase does **not** integrate MAF: that remains Phase 3 by design.

## Compatibility constraints

The original authenticated `/api/tasks/{id}/interview/*` HTTP contract remains stable. Stored conversation roles remain `owner` and `assistant` in SQLite, even when the visible speaker is **Product Owner**. We do not rewrite saved messages or invalidate prior approved specifications.

The existing version/hash-controlled approval and separate source-only project generation flow remain unchanged. **Generated files are not verified builds**.

## Local Ollama model picker (Phase 1 usability fix)

The Product Owner model dropdown is populated by **GET `/api/models`**, an authenticated CodingAssistant API endpoint. The backend calls only `http://127.0.0.1:11434/api/tags` with proxies/redirects disabled; it returns sorted, deduplicated, safe downloaded local model names. The list can be refreshed. Cloud-tagged entries, non-downloaded placeholders and invalid names are rejected; no hosted inference fallback is enabled.

The human owner **explicitly chooses a model** before beginning a new conversation. The server independently confirms that exact name is still installed, both for new/continued interview turns and specification drafting. If a pinned conversation model is removed, the workflow stops with `MODEL_NOT_INSTALLED` rather than switching silently. Stored conversations, approved specifications and the independently qualified M1 executable model pin are unaffected.

Ollama's local installed list can contain embedding-only models. Those may appear in the dropdown but will not be suitable for chat; choose a chat-capable model. If Ollama is offline, the UI shows a refreshable error instead of a fabricated list. On Windows, build the current source solution to update the packaged React UI; reloading an older compiled `wwwroot` does not reflect the latest UI source.

## Deterministic verification

The existing `.github/workflows/m0-tests.yml` builds the React UI and runs real HTTP/SQLite API smoke tests on both Ubuntu and Windows. The smoke suite now explicitly checks historical `assistant` and `owner` roles survive an API restart unchanged. These tests exercise persistence, contracts and UI compilation, but not model response quality.

To run just the API suite locally from a clean checkout:

```sh
dotnet run --configuration Release --project tests/CodingAssistant.Api.Smoke/CodingAssistant.Api.Smoke.csproj
```

## Owner local-model acceptance (required before Phase 1 GREEN)

1. Launch the application with an installed **local Ollama** model; create a fresh project with a **nontrivial, non-demo** objective.
2. Confirm the UI calls the AI **Product Owner** and old saved interviews remain readable.
3. Conduct at least three turns describing the intended users, an important workflow and a changed requirement or contradiction.
4. Observe that the Product Owner asks relevant one-or-two-question follow-ups, does not repeat resolved facts, notices the changed requirement, distinguishes assumptions and identifies actionable acceptance criteria.
5. Draft a specification; check that owner requirements, assumptions, open technical decisions, and acceptance criteria are accurately separated; approve only after human review.
6. Confirm source generation/download still works as before, without mistaking generated text for independently compiled/tested source.

Record the model, OS, commit, observable evidence and outcome. A good model-free CI result alone **does not** satisfy the conversational quality requirement, and past owner acceptance of the older architect/interviewer prompt is not evidence that the new Product Owner prompt works.
