# AI migration assistance plan

Status: plan only. Nothing in this document is implemented yet. It builds on the existing AI authoring assistant and on `docs/connection-assistance-plan.md`, and it complements `BizTalk-Import-Compatibility-Plan.md`.

## Goal

Help a person who does not know BizTalk move an imported BizTalk project to working native Mulse flows. The importer already produces disabled native flows, visible `Blocked` nodes, documented stubs and `Config` placeholders. The AI should shorten the distance between "imported" and "reviewed and trusted":

- explain what the importer found, in plain language;
- decide what to do first;
- propose the specific edit that resolves a blocker or fills a stub;
- generate the tests that show the result is right;
- explain every difference found when real BizTalk output is compared with native output.

The AI is an assistant to the reviewer. It never decides that a flow is equivalent to its BizTalk original.

## Principles

1. **Deterministic first, AI second.** Grouping, ranking, counting, validation and comparison are code. The AI only explains and proposes. Every feature works, at a reduced level, with the AI turned off.
2. **AI output is never proof.** A proposal is checked by the same validation and tests as hand-written work, and it is labelled as AI-suggested. Passing the checks shows the proposal matches the supplied examples, not that it matches BizTalk in general.
3. **Nothing is applied silently.** Proposals are shown as a diff against the unsaved draft, are applied only on an explicit action, and can be undone. Saving, enabling and running stay separate operator actions. Imported flows stay disabled.
4. **Fail closed.** If a proposal does not validate, the user sees why and the node stays as it was. Unknown stays `Blocked`.
5. **Least data.** Raw message data is off by default. Secrets and `Config` placeholder values are never sent. Each category of disclosure is a separate, visible choice, and the exact payload can be previewed.
6. **Local first.** A local Ollama model is the recommended provider for imports, because imported source (orchestrations, maps, policies, bindings) can describe internal systems and can sit next to PHI-adjacent data. Hosted providers remain available and opt-in.
7. **Imported text is untrusted input.** ODX expressions, comments, XSLT, policy text, binding descriptions and message samples can contain text that looks like instructions. The prompt separates them as quoted data, the model has no tools that act on its own, and proposals are validated server-side regardless of what the text said.
8. **Everything is configurable in the UI.** Provider, model, disclosure choices and limits use the existing AI settings page, not new files or flags.
9. **General BizTalk, not one sample.** Prompts, glossary and triage rules describe BizTalk constructs in general. No organization-specific type, schema, map or message is named in code, prompts, tests or documentation. Tests use synthetic text.

## What exists today

- A Designer **AI assistant** with OpenAI, OpenAI-compatible/local (Ollama) and per-user GitHub Copilot providers (`Service\Ai\*`, `Mulse.Ui\Components\Shared\FlowAssistant.razor`, `Designer.razor.Assistant.cs`, `AiAssistance.razor`).
- It is opt-in, redacts what it sends, validates proposals on the server (`AiAuthoringProposalProcessor`), applies them to an unsaved disabled draft and offers **Undo AI proposal**.
- Sample-based **connection assistance** for one JSON source and one `json-render` route, with scenarios and a preview (`docs/connection-assistance-plan.md`).
- A manual **copy/paste prompt** for custom-function stubs (`Mulse.Ui\Models\XmlCustomFunctions.cs` `BuildPrompt`, `XmlCustomFunctionsEditor.razor`). It does not call the configured provider.
- The import report: blocker reasons, warnings, configuration requirements, diagnostics and the construct coverage matrix.

## Gaps

| Gap | Effect |
|---|---|
| The assistant cannot see native flows. The redactor withholds the native `flowJson` setting as free text and sets `context.Migration = null`. | Blocked nodes, block reasons, source expressions and import warnings are invisible to the AI. |
| The system prompt describes only fetch, parse, augment, render and deliver. | The AI does not know the native node kinds (`If`, `ForEach`, `Listen`, `SetVariable`, `InvokeTransform`, `Deliver`, and so on). |
| Stubs are a manual copy/paste prompt. | The user must leave the app, paste text into another tool and bring the result back. |
| There is no blocker overview. | A project with 40 blocked flows is a list of long reasons; nothing says which to do first or which share a cause. |
| There is no AI in the Mapping Designer. | Imported maps with unsupported functoids give the user nothing to start from. |
| No comparison with real BizTalk output. | The only proof of equivalence is manual. |
| No plain-language explanation of BizTalk concepts. | A user who does not know BizTalk cannot judge a warning such as "MessageBox publish/subscribe". |

## Features

Each feature lists what it does, the deterministic part, the AI part, and how the result is checked.

### F1. Blocker triage

- **What:** one view of every blocker and warning across the imported project, grouped by cause and ranked by how many flows each clears.
- **Deterministic:** normalize reasons (strip names), group, count flows affected, sort, and tag each group with its kind: *needs a decision*, *needs information from BizTalk*, *needs a stub implemented*, *needs a module that does not exist yet*. The kind comes from rule tables kept next to the importer, not from the model.
- **AI (optional):** a plain-language paragraph per group: what the construct is, why it was not translated, what the user has to decide, and where to look in BizTalk. Cached per group, shown with a "generated" label.
- **Check:** counts and groups are code. The paragraph carries no actions.

### F2. Explain this (BizTalk glossary and per-node explanations)

- **What:** an "Explain" action on any imported node, warning or placeholder.
- **Deterministic:** a built-in glossary of BizTalk concepts keyed by the importer's own construct names (MessageBox, correlation, transmit pipeline, send port retry, `XMLTransmit`, promoted property, and so on). Works without AI.
- **AI (optional):** explains the specific node in context, in simple terms, including what native behavior differs and what to verify.
- **Check:** the glossary entry is shown first; the AI text is clearly separate.

### F3. Suggest formula for stubs

- **What:** a **Suggest** button on custom-function and scripting-functoid stubs, replacing the copy/paste prompt.
- **Input:** the stub's name, declared inputs and output type, the surrounding map context, and, only if the user opts in, the original source text and sample values.
- **Output:** a proposed formula or C# body plus a **test table** of inputs and expected outputs. The AI proposes the test table too, but the user can edit it and must confirm the expected values.
- **Check:** the formula is evaluated by the existing mapping/formula engine against every row; rows that fail block Apply. A formula that cannot be expressed in installed capabilities produces custom plugin scaffolding instead, clearly marked as custom code.
- **Note:** the stub stays marked as an unreviewed stub until the user confirms the test table.

### F4. Resolve this blocked node

- **What:** on a `Blocked` native node, **Propose a fix** returns replacement native nodes (for example a `SetVariable` sequence, a `Deliver` with chosen settings, or a pipeline expressed as ordered Augment/Render nodes).
- **Context sent:** the node, its block reason, the source expression, neighbouring nodes, declared variables and messages, and the list of native node kinds with their settings. The redactor is extended to keep these and still withhold secrets and `Config` values.
- **Check:** the proposal must parse through the native flow reader, pass the editor's structural checks, and run in the isolated test against the user's scenarios. The proposal is applied to the draft only, and the node is labelled AI-suggested until the user clears the label.
- **Limits:** one node at a time, a bounded neighbourhood, no changes outside the selected subtree.

### F5. Custom pipeline mapper

- **What:** for a send or receive port with a custom pipeline, read the component inventory (already extracted by the importer) and propose an ordered chain of native steps for each component, marking components with no native equivalent.
- **Deterministic:** stage order, known component mappings (the importer already maps HL7 caster, set-property, promotion and a few others).
- **AI:** suggests steps for unknown components from their documented behavior and configuration, with the reasoning shown.
- **Check:** the same as F4. Components the importer cannot prove stay Blocked or a named stub.

### F6. Configuration helper

- **What:** for each `Config` placeholder, show what the value means, where it comes from in BizTalk (binding export, host config, Business Rules), and a suggested value only when it is derivable from imported data.
- **Deterministic:** the requirement description and its source already exist; add a "where to find it in BizTalk" field to the requirement model.
- **AI (optional):** answers questions about a specific placeholder, such as which flow-link channel a MessageBox send should use, given the subscribers found in the imported bindings.
- **Check:** secrets are never requested from or sent to the model; the user types them into the secret store.

### F7. Synthetic sample and test generator

- **What:** generate valid sample messages from the imported schemas and propose scenarios (missing optional fields, empty repeats, boundary values, date/time edge cases) for a flow, map or stub.
- **Deterministic:** a schema-driven generator creates valid and boundary documents with synthetic values. This is the default path and needs no AI.
- **AI (optional):** proposes business-meaningful scenarios and expected outputs from the imported expressions, always as a draft for the user to confirm.
- **Check:** generated data is synthetic and marked as such. No real message data is needed.

### F8. Compare with real BizTalk output

- **What:** the user supplies pairs: the BizTalk input and the BizTalk output captured from the real server. Mulse runs the native flow on the input, compares, and lists differences.
- **Deterministic:** the run and an XML-aware diff (declaration, encoding, byte-order mark, ordering, whitespace, namespaces, date/time format). Differences are classified.
- **AI (optional):** explains each class of difference and proposes an edit through F4 or F3. For example, a missing XML declaration after `XMLTransmit` is a known, explained difference.
- **Check:** a passing comparison is the strongest evidence this tool can produce. It is recorded per flow with the sample name, and it is reported as "matches N supplied samples", never as "equivalent".
- **Data:** the pairs are real data. They stay on the server, are never sent to a hosted provider unless the user opts in per comparison, and are cleared on exit like connection-assistance samples.

### F9. Map review

- **What:** in the Mapping Designer, **Review this map** lists unsupported or stubbed functoids, suspicious links (unlinked required fields, constant-only outputs, type mismatches) and suggested replacements.
- **Deterministic:** the structural checks. The converter already reports why a map failed to convert; surface it as a checklist.
- **AI (optional):** explains each finding and proposes a replacement mapping or stub formula through F3.

### F10. Migration chat with project context

- **What:** the existing chat, but aware of the import: the report, triage groups, selected node, warnings and coverage matrix.
- **Prompt:** the system prompt is extended with the native node kinds and settings, the Blocked/stub conventions, and the rules in this document ("never claim equivalence"). The BizTalk glossary is included on demand, not always.
- **Check:** chat can only produce proposals through the existing validated proposal path.

## Shared engineering work

1. **Context builder for native flows.** Replace the current "withhold `flowJson`, null `Migration`" behavior with a structured, redacted native-flow context: nodes with kinds, names, block reasons and non-secret settings; source expressions (optionally); warnings; configuration requirements without values. Provide a size budget and a per-category disclosure preview.
2. **Native node catalog for prompts.** Generate it from `NativeFlowNodeKind` and the module descriptors, not by hand, so it cannot drift.
3. **Proposal pipeline for native flows.** Extend `AiAuthoringProposalProcessor` with native proposals: parse with the native flow reader (blocked nodes allowed in the draft, not in what the model may leave behind for the selected subtree), validate, diff, apply to the draft, Undo.
4. **Provenance.** Add `source` (`import`, `ai`, `user`) and `reviewed` to native nodes and stubs. The editor shows an "AI-suggested, unreviewed" badge. A flow with unreviewed AI nodes cannot be enabled without a visible acknowledgement.
5. **Prompt-injection handling.** Imported text goes into clearly delimited data blocks; fixed instructions come first and last; server-side validation is the control, not the prompt. Include injection test cases (comments in an expression, a policy name, a binding description).
6. **Disclosure controls.** Categories: structure only (default), source expressions, sample values, real comparison data. Each is a separate checkbox, with a preview of the exact payload and a size count. Remember choices per session, not permanently.
7. **Audit trail.** For each proposal record: time, user, provider and model, categories disclosed, the proposal, whether it was applied, and test results. No secrets, no sample values. Kept with the flow history.
8. **Cost and size control.** Token budget per request, batching for triage, caching of triage and explanation text keyed by group and importer version.
9. **Offline behavior.** If no provider is configured, F1, F2 (glossary), F7 (deterministic), F8 (comparison) and the F9 checklist still work.

## Phases

Each phase ends with working, tested behavior and a plan-document update. Each is small enough to commit on its own.

| Phase | Contents | Exit criteria |
|---|---|---|
| **A. Foundations** | Native-flow context builder, redactor changes, native node catalog, disclosure preview, prompt text for native flows (F10 prompt only). | The assistant can describe a blocked node and its reason without sending secrets or `Config` values. Redaction tests, injection tests. |
| **B. Triage and explain** | F1 deterministic triage view, F2 glossary, optional AI paragraphs. | The Billing sample's blockers appear as grouped, ranked, explained rows with and without AI. |
| **C. Stub formulas** | F3 with the test table and engine check, replacing the copy/paste prompt. | A stub gets a proposal, the table runs, failing rows block Apply, Undo works. |
| **D. Resolve a blocked node** | F4 with provenance and the unreviewed-AI acknowledgement. | A blocked `Send`, `Terminate` or `SetVariable` node is replaced through a validated proposal in the isolated test. |
| **E. Compare with BizTalk output** | F8 deterministic comparison first, then the AI explanation of differences. | A supplied pair produces classified differences; a passing pair is recorded as "matches N samples". |
| **F. Test and config helpers** | F7 deterministic generator, F6 requirement guidance. | Valid and boundary samples from imported schemas; every `Config` placeholder explains its source. |
| **G. Pipelines and maps** | F5 and F9. | A custom pipeline inventory yields a proposed chain; a map review checklist lists unsupported functoids. |

Recommended order: A, B, C, E (the deterministic comparison first), D, F, G. Triage and stub formulas help the most imported projects soonest; the comparison gives the only real evidence of correctness.

## Testing

- **Unit and integration tests** use fake chat clients that return fixed proposals, including malformed, oversized, hostile and out-of-scope ones. Validation, redaction and Undo are tested without any real model.
- **Redaction tests** confirm that secrets, `Config` values, passwords and sample values are absent from the payload unless a category is enabled, and that enabled categories are exactly what the preview showed.
- **Injection tests** put instruction-like text in expressions, names, comments and descriptions and confirm the proposal is still validated against the allowed scope.
- **Evaluation set.** A small, synthetic suite of blockers and stubs with known good outcomes, run on demand against a configured provider to compare models. It reports pass rate on validation and on the test table. It is opt-in and never run in the normal test suite.
- **UI tests** render the new panels and the badges, and cover the disclosure preview.

## Risks and how they are handled

| Risk | Handling |
|---|---|
| A confident but wrong proposal is trusted. | Test table and isolated test must pass; "AI-suggested, unreviewed" badge; acknowledgement before enabling; no equivalence claims in any text. |
| Data leaves the organization. | Structure-only default, per-category disclosure, payload preview, local provider recommended, hosted providers opt-in, audit trail. |
| Untrusted text steers the model. | Delimited data blocks, no autonomous tools, server-side validation of every proposal. |
| Cost or latency on large projects. | Deterministic triage first, batching, caching, budgets. |
| Prompt text drifts from the product. | The node catalog is generated from code; prompt tests assert it lists every node kind. |
| The feature is useful only for one sample. | Glossary and rules are written for BizTalk in general; synthetic tests cover constructs the sample does not use. |

## Decisions needed

1. **Provider default for imports.** Recommend local Ollama and show a notice when a hosted provider is chosen. Is that acceptable as a default behavior?
2. **Source expressions disclosure.** They carry business logic but can embed literals. Should the default be "structure only, expressions on request", as proposed?
3. **Audit trail location.** Store it with the flow history (proposed) or in a separate log?
4. **Comparison data retention.** Keep supplied BizTalk input/output pairs for the session only (proposed), or allow saving them as named test scenarios on the flow?
5. **Acknowledgement before enabling.** Block enabling a flow with unreviewed AI nodes until acknowledged (proposed), or warn only?

## Out of scope

- Any feature that auto-saves, auto-enables or auto-runs a flow.
- Treating a model's statement of equivalence as evidence.
- Training or fine-tuning on user data.
- Executing BizTalk artifacts or hosting BizTalk code.
