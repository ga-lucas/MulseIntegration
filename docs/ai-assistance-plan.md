# AI assistance plan

Status: partly built. Built so far: the BizTalk wizard assist, the compare-with-real-output assist and saved scenarios, and the first Phase A/B slice: the surface registry (`/api/assist/surfaces`, which records what each part of each surface may send and never allows config values or secrets), the step-kind catalog, the BizTalk glossary, and local "explain this flow / step" facts (`/api/assist/explain/*`, no AI provider needed, setting values never returned), shown in the native flow editor as the "Explain this flow" panel with a BizTalk term lookup, and a locally built "write a missing module" prompt (`/api/assist/module-prompt`): for a module the flow uses that is not installed, it lists the module id, kind, setting names and usage, plus the module contract rules from `Module-Developer-Guide.md`, never setting values, and the user copies it to any AI tool. Also built: a structure-only redactor for native flows, config references and run records (`AssistRedactor`; names and set/not-set states only, never values, with best-effort scrubbing of error text and opt-in block reasons); an append-only AI activity log (`Data\ai-activity`, shown on the AI assistance page and at `/api/assist/activity`; it records time, user, surface, provider, model, categories, flow id, a request length and hash and the outcome, never the text sent or the reply, and it does not record whether a proposal was later applied); the list of config values and secrets a flow refers to (names only); and AI wording of the flow explanation (`/api/authoring/flow-wording`, a previewed, hash-bound send that always asks again when blocked-step reasons are included); and the deterministic "Explain this run" on the History page (`/api/assist/explain/run/{executionId}`, O1 and O2 without AI: what the run state and each delivery state mean, why Mulse did not retry, and what a person should check by hand; it never repeats error text or message content and never offers a retry or resend), plus AI wording of a run (`/api/authoring/run-wording/{executionId}`, the same previewed, hash-bound send; the server reads the run itself, only states and stage names are sent by default, scrubbed error text is opt-in and always asks again, it is logged on the `history` surface and the prompt forbids advising a retry or resend), and the deterministic failure patterns across flows (O3, `/api/assist/explain/failures`, shown on the dashboard: recent failed, held and retrying runs grouped by likely cause and stage with fixed keyword rules, with the one change that would clear the most; error text is read only to pick a category and is never returned, and it never offers a retry or resend), and the deterministic operational digest (O4, `/api/assist/digest?hours=24`, shown on the dashboard: runs by state and success rate for 24 hours, 3 days or 7 days, held runs, flows that ran slower than usual, and enabled Interval flows that have not started a run for more than three intervals; it reads only states, times and trigger settings, and never offers a retry or resend), and the deterministic pre-enable flow review (R1, `/api/assist/review/{flowId}`, shown on the Flows page as the "Review" button and as a review step when Enable is clicked: rules for blockers, warnings and notes with names and counts only, a list of what was not checked, advisory only; it never enables anything itself). Not built yet: the "unreviewed AI" badge and enable gate and the "untested branches" check (they need `source` and `reviewed` on native nodes and scenario links, which arrive with AI edits of native nodes), R2 (change summary on save), R3 (flow documentation), optional AI wording of the review, the patterns and the digest, and the rest of phases D to H. It builds on the existing AI authoring assistant and on `docs/connection-assistance-plan.md`. BizTalk migration is one area of this plan (see "Migrating from another system"); the plan covers the whole product.

## Goal

Help every kind of user get more done in Mulse, with less specialist knowledge, at every stage of a flow's life:

| Stage | The user wants to... |
|---|---|
| Understand | know what a flow, node, module or setting does, and what a run did. |
| Build | go from a description or sample data to a working flow, mapping or formula. |
| Configure | fill in settings, config values and secrets correctly. |
| Test | prove a flow does the right thing, without real data. |
| Operate | find out why a run failed or is held, and what to do about it. |
| Review | check a flow is safe before it is enabled, and understand a change. |
| Extend | write a custom module or plugin. |
| Migrate | bring an existing integration (such as BizTalk) across and finish what the importer could not. |

Users are flow authors who may not be developers, operators who support running flows, module developers, and administrators. The assistant adapts its language to what the user is doing, not to a role setting.

The AI is an assistant to the person responsible. It never decides that a flow is correct, safe or equivalent to anything.

## Principles

1. **Deterministic first, AI second.** Counting, grouping, validation, diffing and running are code. The AI explains, proposes and answers questions. Every feature has a useful reduced form with the AI turned off.
2. **AI output is never proof.** A proposal is checked by the same validation and tests as hand-written work. Passing shows the proposal matches the supplied examples, not that it is right in general.
3. **Read-only features come first.** Explaining, diagnosing and reviewing change nothing, so they carry the least risk and ship first. Proposals that change a draft come next.
4. **Nothing is applied silently.** Proposals are shown as a diff against the unsaved draft, applied only on an explicit action, and can be undone. Saving, enabling and running stay separate actions. The AI never retries, resends, acknowledges, deletes or reconciles anything on its own.
5. **Fail closed.** A proposal that does not validate is shown with the reason, and nothing changes.
6. **Least data.** Structure only is the default disclosure. Source expressions, settings values, run logs, message data and sample values are each a separate, visible choice with a payload preview. Secrets and config values are never sent.
7. **No default provider.** AI is off until an administrator chooses a provider and model on the settings page; nothing is preselected. A local model (Ollama) is supported and the documentation explains why it suits sensitive systems, but it is not the default. Choosing a hosted provider shows a notice stating what categories of data can leave the organization.
8. **All text from outside is untrusted.** Message content, file names, error text, run logs, imported source, descriptions and comments can contain text that looks like instructions. It is passed as delimited data, the model has no tools that act on its own, and server-side validation is the control.
9. **The assistant has the user's permissions and no more.** It reads only what the signed-in user can open, and proposals go through the same authorization as manual edits.
10. **Everything is configurable in the UI.** Provider, model, disclosure categories, limits and which features are on use the existing AI settings page.
11. **UTC and invariant culture** apply to anything the assistant computes or shows from the backend. The UI follows the global display toggle.
12. **General, not sample-specific.** Prompts, glossaries and rules describe constructs in general. Tests use synthetic data.

## What exists today

- A Designer **AI assistant** with OpenAI, OpenAI-compatible/local (Ollama) and per-user GitHub Copilot providers (`Service\Ai\*`, `Mulse.Ui\Components\Shared\FlowAssistant.razor`, `Designer.razor.Assistant.cs`, `AiAssistance.razor`).
- It is opt-in, redacts what it sends, validates proposals on the server (`AiAuthoringProposalProcessor`), applies them to an unsaved disabled draft, and has **Undo AI proposal**.
- Sample-based **connection assistance** for one JSON source and one `json-render` route (`docs/connection-assistance-plan.md`).
- A manual **copy/paste prompt** for custom-function stubs (`Mulse.Ui\Models\XmlCustomFunctions.cs`).
- Other surfaces with no AI today: History (`/history`), Config values, Modules and Module Packages, Storage, Backup & Restore, the Mapping Designer, the native flow editor, the import report.

## Gaps

| Gap | Effect |
|---|---|
| The assistant exists only inside the flow Designer. | Nothing helps on History, Config values, Modules, Mapping Designer or the import report, where users get stuck. |
| It sees only the classic flow shape (fetch, parse, augment, render, deliver). The native `flowJson` setting is withheld and `context.Migration` is nulled. | Native flows, their nodes and any import blockers are invisible to it. |
| The prompt describes a fixed list of node kinds by hand. | It can drift from the product and does not know the native node kinds. |
| No run or failure context. | It cannot explain why a run failed, is held, or has an ambiguous effect. |
| Connection assistance is JSON to JSON only. | XML, CSV, flat-file, HL7 and Excel flows get no mapping help. |
| No product help. | Users read a 2,300-line README to learn what a setting means. |
| Stubs are a manual prompt. | The user leaves the app to get help and pastes the result back. |
| No pre-enable review, no change summary, no generated documentation. | Mistakes (a poller pointed at its own output, missing retries, secrets in plain settings) are found in production. |

## Capabilities

Each lists what the user gets, the deterministic part, the AI part, and the check.

### Understand

**U1. Explain this.** An action on any flow, node, setting, module, config value, warning or history entry. Deterministic: the module descriptor text and setting descriptions already exist; show them first. AI (optional): explains the specific node in its context, in plain language, including side effects (what it reads, writes, deletes or acknowledges).

**U2. Ask about my flows.** Natural-language questions over the saved flows: "which flows write to this folder?", "what runs every night?", "which flows use this config value?". Deterministic: an index of flows, modules, settings keys and references drives the answers; the AI turns the question into a query over that index and words the answer. It cites the flows it used. Results are links, not claims.

**U3. Ask the docs.** Contextual help grounded in the product documentation (README, Module Developer Guide, module descriptors). Answers cite the section. If the documentation does not cover it, the assistant says so.

**U4. Explain a flow end to end.** A plain-language summary of a whole flow: trigger, sources, transformations, routes, side effects, retries and failure behavior. Generated from structure; the AI wording is separate from the facts table.

### Build

**B1. Describe-to-draft.** Existing flow authoring from a description, extended to native flows and to every installed module, using a prompt catalog generated from module descriptors and `NativeFlowNodeKind`, not written by hand.

**B2. Connection assistance, all formats.** Extend the connection assistance beyond JSON: raw samples parsed with the configured parser (XML/XSD, delimited/flat file, HL7, Excel), authoritative schemas as input, constants, conditions and installed conversion transforms, multiple sources and existing augments.

**B3. Mapping Designer assistant.** Suggest field matches from names, types and samples; explain and repair unmapped required fields and type mismatches; propose transform chains for a described conversion. The mapping is run against the supplied scenarios before it can be applied.

**B4. Formulas and conditions.** "Suggest" on any expression, condition or custom function: a proposal plus a test table of inputs and expected outputs. The table is run by the real engine; failing rows block Apply. If installed capabilities cannot express it, the result is custom plugin scaffolding, clearly marked as custom code.

**B5. Triggers and schedules.** Describe when a flow should run in words; the assistant proposes an interval or trigger, checks it against the flow (for example a poller that reads its own output) and shows the next run times in the UI's time display.

**B6. Native flow editing.** Select a node (or a blocked node), describe the change, receive replacement nodes. Checked by the native flow reader, the editor's structural checks and the isolated test. Limited to the selected subtree.

### Configure

**C1. Configuration helper.** For every `Config` and secret placeholder: what it means, where its value normally comes from, and a suggested value only when it is derivable from existing flow data. Secrets are never requested from or sent to the model; the user types them into the secret store.

**C2. Settings checks.** Highlight likely mistakes in module settings before saving: a pattern that matches nothing, a path that is relative where the module needs absolute, a retry count that conflicts with an atomic scope, credentials typed into a plain setting instead of a stored value (with a one-click move to the stored value).

**C3. Config values page.** Find unused, duplicate or missing config values across flows, and explain each reference.

### Test

**T1. Synthetic samples.** Generate valid and boundary samples from schemas or inferred shapes: missing optional fields, empty repeats, large values, special characters, date/time edge cases (UTC and offset forms). Deterministic generation is the default; the AI proposes business-meaningful variations.

**T2. Scenarios from history.** Turn a past run into a scenario: input, expected output and the settings used. Message content is real data, so this uses redaction (replace values with synthetic ones keeping shape) before anything can be sent to a model.

**T3. Regression check on change.** Re-run saved scenarios when a flow or map changes and explain which outputs changed.

#### Saved real-data scenarios

Scenarios built from history (T2) and BizTalk comparison pairs (M6) can be saved by the user. Because they hold real data, saving is explicit and has rules:

- A saved scenario is marked **contains real data**, shows who saved it and when, and can be deleted at any time.
- It is stored in workspace storage, separate from the flow definition, so exporting or copying a flow does not carry it.
- It is never sent to a model unless the user enables the message-data category for that request and sees the payload preview. Synthetic copies (values replaced, shape kept) can be saved without the marker and used freely.
- It is **not** included in Backup & Restore or flow exports (implemented decision for comparison scenarios; the encrypted store stays on the service machine).
- Retention is chosen per scenario (keep until deleted, or expire after 1 to 365 days). Expired scenarios are deleted, not archived.
- Results (pass or fail and the classified differences) are kept with the scenario; the data itself is not copied into the audit trail.

### Operate

**O1. Explain this run.** On a history entry: a plain-language account of what happened, where it stopped, and the error in context ("the SFTP fetch could not connect: host name did not resolve"). Deterministic: the run record, the failing step and the error category. AI (optional): wording, likely causes ranked, and what to check. Error text is redacted by default.

**O2. Held and ambiguous work.** For work that is held or whose effect is ambiguous (a delivery that may or may not have completed), the assistant explains what the state means, why Mulse did not retry, and how to check the destination by hand. It never retries, resends or reconciles. It can explain a Deliver node's configured retry (including the "retry ambiguous failures" opt-in and its duplicate risk), and can propose a change to it only through the normal diff-and-apply path.

**O3. Failure patterns.** Group recent failures by cause across flows and say which one change would clear most of them. The grouping is code; the summary is optional AI. Built (deterministic part): the last failed, held or retrying runs (up to 200) are grouped by category and stage. The category comes from fixed keyword rules over the stored error text (timeout, cannot reach host, authentication or permission, missing file or path, file locked, unresolved configuration, database error, disk or quota, invalid input data), plus "ambiguous delivery" for held runs with an uncertain delivery, "no error detail recorded" and "other (not recognised)". The report names the category, stage, run count, share, flows, a what-to-check line and a suggested change, and says plainly when no single cause stands out. It is a starting point, not a diagnosis. Optional AI wording of the report is not built.

**O4. Operational summary.** A short digest of a period: runs, failures, held items, slow flows, flows that stopped running. Facts first; text second. Built (deterministic part): counts by state and a success rate for the chosen period; every held run, even one that began earlier; a flow is "slower than usual" when its latest completed run took more than twice its usual time (and at least five seconds longer), judged from at least four completed runs in the period; an enabled Interval flow "may have stopped" when no run started for more than three intervals (on-demand and push flows are not checked). The sentences are generated from those facts by fixed rules and the digest says how many runs it read (at most the latest 200). Optional AI wording is not built.

### Review

**R1. Pre-enable review.** Before a flow is enabled, a checklist: sources and destinations that point at each other, no source acknowledgement or cleanup where one is expected, deliveries without retries, secrets in plain settings, unresolved placeholders, untested branches, stubs and AI-suggested nodes that no one has reviewed. The checks are rules in code. The AI explains each finding and proposes a fix through the normal proposal path.

**R2. Change summary.** On save, a summary of what changed and its effect ("this flow now also writes a copy to the archive folder"), plus a warning for risky changes. The diff is code; the wording is optional AI.

**R3. Flow documentation.** Generate a runbook per flow: purpose, schedule, inputs, outputs, dependencies, config values used, failure behavior, who to contact (user-supplied). Facts come from structure; the AI writes the prose.

### Extend

**X1. Custom module scaffolding.** From a description and the Module Developer Guide, generate a module project skeleton and unit tests that follow the module contracts and settings descriptors. The output is a download for the developer to build; Mulse never installs or runs it automatically.

**X2. Module explain and review.** For a loaded module package, explain what a module's settings and behavior are and flag descriptors that are missing or unclear.

### Migrating from another system

BizTalk migration is the first migration area; the importer, its blocked nodes, stubs and placeholders are described in `BizTalk-Import-Compatibility-Plan.md`. Features here are written against general importer results (blockers, warnings, requirements, stubs), not BizTalk types, so a later importer can reuse them.

**M1. Blocker triage.** Group and rank blockers and warnings across a project by cause and by number of flows cleared; tag each group (needs a decision, needs information from the source system, needs a stub implemented, needs a module that does not exist). Rules are code; plain-language explanations are optional AI, cached per group.

**M2. Glossary and explain.** A built-in glossary of source-system concepts (for BizTalk: MessageBox, correlation, transmit pipeline, send port retry, `XMLTransmit`, promoted property) shown first, with optional AI explanation of the specific node.

**M3. Stub formulas** are B4 applied to imported stubs (scripting functoids, helper calls).

**M4. Resolve a blocked node** is B6 applied to `Blocked` nodes, with the block reason and source expression as context.

**M5. Custom pipeline mapper.** Propose an ordered chain of native steps from a custom pipeline's component list; components with no known equivalent stay blocked or a named stub.

**M6. Compare with real output.** The user supplies pairs: source-system input and the output captured from the real server. Mulse runs the native flow on the input and compares. Deterministic: an XML-aware diff (declaration, encoding, byte-order mark, ordering, whitespace, namespaces, date/time format) with each difference classified. Optional AI: explains each class and proposes an edit through B4 or B6. A passing comparison is the strongest evidence the tool can produce, and it is reported as "matches N supplied samples", never as "equivalent". The pairs are real data and follow the strictest disclosure rules. The user can save a pair as a named **comparison scenario** (see "Saved real-data scenarios" below), so the comparison can be re-run after a change.

**M7. Map review.** A checklist of unsupported or stubbed functoids and suspicious links in an imported map, with proposed replacements through B3 and B4.

## Where it appears in the UI

- One **assistant panel** that follows the user, with the current page, selection and permitted context. It replaces the Designer-only panel.
- **Contextual actions** next to the thing they act on: Explain (nodes, warnings, settings, history entries), Suggest (expressions, formulas, mappings), Review (before enabling, on save), Compare (imported flows).
- Each page shows what the AI will receive for that action, with the disclosure categories and a payload preview.
- The AI features are visibly optional: turning the provider off hides AI text and leaves the deterministic parts (checklists, groupings, glossary, comparisons).
- Keyboard and screen-reader access matches the rest of the UI; AI text is plain text with a clear "generated" label.

## Shared engineering work

1. **Context surfaces.** A registry of surfaces (Designer, native editor, Mapping Designer, History, Config values, Modules, import report), each declaring what context it can provide, which disclosure category each part belongs to, and its size budget. This replaces the single draft redactor.
2. **Redaction.** Extend the redactor to native flows, run records and config references. Keep secrets, config values and message data out unless a category is enabled. Test each category.
3. **Generated catalogs.** Module descriptors and `NativeFlowNodeKind` feed the prompt catalog, so it cannot drift. A test fails if a node kind or module kind is missing from it.
4. **Proposal pipeline.** One pipeline for every proposal kind (flow draft, native subtree, mapping, formula with test table, config requirement list, documentation): parse, validate, diff, apply to the draft, Undo. Reuse `AiAuthoringProposalProcessor`.
5. **Provenance.** Native nodes, stubs and mappings carry `source` (`import`, `ai`, `user`) and `reviewed`. The editor shows an "AI-suggested, unreviewed" badge, and R1 lists unreviewed items. Enabling a flow with them requires a visible acknowledgement.
6. **Prompt-injection handling.** Delimited data blocks, fixed instructions before and after, no autonomous tools, validation on every proposal. Injection test cases come from every kind of untrusted text (messages, file names, errors, expressions, descriptions).
7. **Audit trail.** A separate, append-only **AI activity log** per workspace, linked to flows by flow ID and shown as a tab on a flow and on the AI settings page. Per request: time, user, surface, provider and model, categories disclosed, proposal, whether it was applied, and check results. No secrets and no sample values. It is separate from run history because it records authoring and help requests (including on pages that are not tied to a flow, such as Config values), it should not be pruned with run history, and run-history retention is a different policy.
8. **Limits and cost.** Token budgets per surface, batching for grouped analysis, caching of explanations keyed by input and product version, and a visible usage summary on the settings page.
9. **Workspaces.** AI settings, disclosure defaults and audit trail are per workspace if multiple workspaces are ever supported.
10. **No-provider mode.** Every deterministic feature (indexes, checklists, groupings, glossary, comparisons, synthetic sample generation) works with no provider configured.

## Phases

Each phase ends with working, tested behavior and an update to this document. Read-only features lead because they cannot change anything.

| Phase | Contents | Exit criteria |
|---|---|---|
| **A. Foundations** | Surface registry, extended redaction (native flows, runs, config references), generated catalogs, disclosure preview, audit trail, provider notices. | The assistant can describe a native flow and a run record without secrets or config values; redaction and injection tests pass. |
| **B. Understand** | U1, U3, U4, plus U2's index. M2 glossary. | Explain works on any node, setting and warning, with and without a provider; docs answers cite sections. |
| **C. Operate** | O1, O2, O3, O4. | A failed, held and ambiguous run each get an accurate explanation that never offers an automatic retry. |
| **D. Review** | R1, R2, R3 and the provenance badge. | A flow with a known mistake is flagged before enabling; the change summary matches the diff. |
| **E. Build and map** | B1, B2, B3, B4, B6 with the shared proposal pipeline. | A proposal for each kind validates, runs its test table or scenarios, applies to the draft and undoes. |
| **F. Configure and test** | C1, C2, C3, T1, T2, T3, B5. | Every placeholder explains its source; synthetic samples come from schemas; scenarios re-run on change. |
| **G. Migration** | M1, M4, M5, M6, M7 (M3 is B4). | The imported sample's blockers appear grouped and ranked; a supplied real-output pair yields classified differences. |
| **H. Extend** | X1, X2. | Generated module scaffolding builds against the contracts and its tests run. |

Recommended order: A, B, C, D, E, F, G, H (confirmed: Foundations, then Understand).

## Testing

- **Fake chat clients** return fixed proposals, including malformed, oversized, hostile and out-of-scope ones. Validation, redaction, undo and audit are tested with no real model.
- **Redaction tests** confirm secrets, config values, message data, run logs and sample values are absent unless their category is on, and that the payload is exactly what the preview showed.
- **Injection tests** put instruction-like text in messages, file names, errors, expressions, descriptions and comments.
- **Deterministic feature tests** (indexes, checklists, groupings, comparisons, generators) run without any provider.
- **UI render tests** cover the panel, the contextual actions, the preview and the badges.
- **Evaluation set.** A small synthetic suite with known good outcomes, run on demand against a configured provider to compare models. Opt-in and never part of the normal test run.

## Risks

| Risk | Handling |
|---|---|
| A confident but wrong answer is trusted. | Facts shown separately from AI text; test tables and scenarios must pass before Apply; unreviewed badge and acknowledgement before enabling; no claims of equivalence or correctness. |
| Data leaves the organization. | Structure-only default, per-category disclosure, payload preview, local provider support, audit trail. |
| Untrusted text steers the model. | Delimited data, no autonomous tools, validation of every proposal. |
| The assistant causes an operational action. | It has no action tools. Retry, resend, acknowledge, delete and reconcile stay manual. |
| Cost or latency at scale. | Deterministic grouping first, batching, caching, budgets, usage summary. |
| Prompts drift from the product. | Catalogs are generated and tested. |

## Decisions

Decided:

1. **Provider.** There is no default provider. AI stays off until one is chosen. A notice appears for hosted providers.
2. **Disclosure.** Structure only by default; source expressions and the other categories are sent only on request, per request.
3. **Audit trail.** A separate AI activity log per workspace, linked to flows (see Shared engineering work, item 7). You were unsure; this is the recommended option and can be changed before it is built.
4. **Saved real-data scenarios.** The user can save comparison pairs and history-derived scenarios, under the rules in "Saved real-data scenarios".
5. **Enable gate.** A flow with unreviewed AI items cannot be enabled until the user acknowledges them.
6. **Who may use AI.** Any signed-in user, as long as an administrator has enabled and configured AI assistance. The application's authentication layer and the AI settings are the only controls; no separate AI permission is added at this time. Principle 9 still applies (the assistant has the user's own permissions and no more), and a per-user or per-role permission can be added later without changing the features.
7. **First phase.** Foundations, then Understand (the order in the phases table). The deterministic migration parts stay in Phase G.

## Out of scope

- Any feature that auto-saves, auto-enables, auto-runs, retries, resends or reconciles.
- Treating a model's statement of correctness or equivalence as evidence.
- Training or fine-tuning on user data.
- Executing source-system artifacts or hosting them.
