# Connection assistance plan

Status: implementation started. Phase 1 has an initial, deliberately limited JSON-to-JSON slice; the full plan is not complete.

## Delivered initial slice

- Open **AI assistant > Assist a connection with sample data** in the designer. Select a parsed source and renderer using the panel selectors; there is no canvas context action yet.
- Supported scope: exactly one parsed source, one `json-render` route, no existing augments or dependent fetches. Other configurations are explicitly rejected rather than changing unrelated stages.
- Supply 1-8 uniquely named scenarios with parsed JSON input (object or array of objects) and expected mapping output (array of objects). Multiple records and nested/repeating data are supported.
- Analyze samples on the Mulse Service without a model call. Review combined shapes, observed optional/null fields, conflicts, and the exact shape/example disclosure. Conflicting types, all-null fields, and array shapes with no observed items block generation until richer or normalized samples are supplied.
- Sample values are off by default, with separate input/expected-output sharing choices per scenario. The model receives only shapes, individually approved values, and the operator's instructions, not the draft, its settings, scenario names, or history.
- Generate direct field-copy and object/array-template mappings. Constants, conditions, conversion transforms and custom plugin execution are explicitly unsupported in this slice.
- Run the existing pure mapping engine against every supplied scenario. Any error or JSON difference blocks Apply. This checks mapping output supplied to the renderer, not complete rendered files or external side effects.
- Server-construct one mapping augment; reuse existing validation, disabled-unsaved-draft application, stale-snapshot checks, delivery protections and Undo AI proposal. No automatic save/run/install occurs.
- Samples are request/session memory only and cleared from the panel on exit. Limits: 32,768 characters per input/output, 16 JSON levels, 20,000 values combined, 256 inferred paths per side, 150,000 characters of combined provider disclosure, and 131,072 characters per returned preview.

Remaining Phase 1 work: raw-file parsing through configured parsers; authoritative schema input; richer explicit contract/conflict resolution; installed conversion transforms; broader renderer support and existing-augment handling; and canvas entry points. Phases 2-4 remain planned. Follow-up work should extend the supported subset explicitly rather than weakening scope, consent or preview checks.

## Goal

Help users select a source/parsed node and a renderer (and later other supported node combinations), describe the desired result, and obtain reviewed mappings, intermediate augment suggestions, or custom C# plugin scaffolding.

Integrate this workflow into the existing authoring assistant. Do not add another top-level AI menu entry.

## User experience

1. Select a starting node and choose **Assist connection...**, then select a compatible destination. Provide source/destination selectors as an alternative to canvas gestures.
2. Describe the intended result and provide representative samples or authoritative schemas for the selected inputs and target.
3. Inspect the inferred shapes, sharing preview, missing information, and business-rule questions before requesting AI assistance.
4. Review the proposed mappings, transforms, required intermediate augments, affected scope, and unresolved work. Clearly distinguish installed capabilities from custom-code requirements.
5. Preview supported mappings against all supplied scenarios and compare actual versus expected output.
6. Explicitly apply supported changes to the unsaved draft, or download custom plugin scaffolding. Saving, enabling, installing plugins, and executing flows remain separate operator actions.

## Samples and shape requirements

- Require representative samples wherever field-level mapping depends on data shape, unless an authoritative schema already supplies that shape.
- Require input evidence for each selected parsed source, plus an expected-output example or authoritative target schema.
- Support multiple named samples per input and expected output. One representative sample is sufficient to start; encourage additional cases for missing fields, nulls, empty collections, repeated records, and conversion edge cases.
- Distinguish raw source samples from parsed samples. Raw Excel/XML/etc. samples can be parsed locally using the configured parser, with the resulting shape shown before any AI request. Parsing is an explicit, bounded operation, not a flow run or source fetch; surface unsupported formats and parse failures.
- Infer a combined shape from all samples, including observed optional fields, nullability, nested objects, repeating arrays, and conflicting types.
- Flag conflicting types and ambiguous structures for user decisions rather than silently selecting a type. Empty arrays require a richer sample or explicit item schema when their item shape is needed.
- Ask explicitly about conversions, defaults, missing-field behavior, lookups, and other business rules that samples cannot establish.
- A renderer's format alone does not establish its business fields. Request target evidence rather than inventing a contract.
- Clearly label inferred shapes as observations, not guarantees about all production data.

## Scenarios and verification

- Pair input samples with expected outputs as named test scenarios.
- For joins, group samples from each participating source into one scenario; do not infer pairings from upload order or filenames.
- Preview the proposed mapping against every supplied scenario, with differences and failures reported individually. If no exact expected output is supplied, report shape/schema checks without claiming output equivalence.
- Report unsupported operations and unresolved business rules explicitly; do not present incomplete previews as successful verification.
- Keep external delivery out of previews. Custom plugin code is not compiled or executed automatically.
- Carry explicitly approved, sanitized scenarios into downloaded plugin fixtures and test skeletons. Unknown behavior remains visibly unimplemented.

## Privacy and consent

- Uploading a sample does not automatically share its values with the AI provider.
- Default AI context to locally inferred structure and types. Show a disclosure preview for schemas, field names, mapping expressions, and any included example values.
- Sharing sanitized example values requires explicit consent for each sample; existing environment-setting consent does not authorize sample-value disclosure.
- Preserve existing setting redaction and never resolve secret/config references for model context.
- Do not automatically attach execution histories, customer payloads, or unrelated flows.
- Explain that sanitization is not guaranteed secret detection and that provider retention policies apply to information users approve for sharing.
- Define sample size/count/depth limits and temporary retention/cleanup behavior before implementation. Avoid adding persistent sample storage implicitly.

## Runtime and proposal constraints

- Respect actual runtime semantics: augments currently form an ordered chain, not arbitrary per-edge branches. Explain which downstream stages a proposed augment affects instead of drawing a misleading isolated connection.
- Prefer installed modules and transforms. Suggest a custom field-transform plugin for a small conversion and a full augment only for batch/message-level behavior.
- Bind proposals to the selected scope and current draft. Reject stale proposals and out-of-scope changes; preserve unrelated edits, identity, migration provenance, and delivery protections.
- Reuse the existing server-side validation, explicit review/apply, disabled unsaved drafts, and guarded undo behavior.
- Never automatically save, enable, execute, compile, install, or load generated code.
- Generated plugins run as trusted server code, not sandboxed code. Make that trust boundary explicit.
- Unfinished implementation stubs fail closed with explicit exceptions, never silent pass-through behavior.

## Implementation phases

### Phase 1: Scoped, sample-informed mapping assistance

- Select one parsed source and one renderer.
- Add multiple named input/expected-output samples or authoritative schemas, local shape inference, conflict resolution, and a disclosure preview.
- Generate focused mapping proposals using installed transforms and existing mapping representations.
- Validate scope and runtime compatibility; support review, apply to unsaved draft, and undo.
- Include explicit previews for supported mappings across all supplied scenarios. Expand preview capabilities in Phase 4 rather than claiming unsupported verification.

Acceptance criteria:

- Missing or ambiguous required shape evidence blocks mapping generation with actionable guidance.
- Sample values are not included in provider requests without explicit sample consent.
- Multiple samples contribute to inferred shapes; type conflicts are surfaced rather than silently coerced.
- Proposals cannot alter unrelated nodes or bypass existing authority/delivery guards.
- Every supplied scenario has an individual preview result or explicit unsupported/blocking explanation.
- Applying does not save or run the flow; stale proposals cannot overwrite newer edits.

### Phase 2: Custom plugin scaffolding

- Offer a downloadable C# project for verified capability gaps.
- Include the appropriate transform/augment contract, module descriptor, installer registration, settings descriptors, test skeleton, approved sanitized fixtures, and build/load instructions.
- Follow existing SDK/sample-plugin conventions and reuse suitable scaffolding patterns without coupling the feature to BizTalk migration.
- Keep unresolved implementation explicitly unimplemented and fail closed. Do not automatically wire an unavailable plugin into an executable flow.

Acceptance criteria:

- Generated scaffolds build against the supported Mulse SDK and register the intended module kind.
- Incomplete runtime implementations fail explicitly; unfinished tests do not falsely pass.
- Only approved fixtures are included, and downloads contain no resolved credentials or unintended sample values.
- Building, reviewing, deploying, loading, and testing remain explicit operator steps.

### Phase 3: Additional supported pairs

- Extend to parsed source -> augment, augment -> renderer, and multi-source joins.
- Define supported context, scope, shape evidence, and application rules per pair.
- Add other combinations only where actual runtime semantics support them.

Acceptance criteria:

- Unsupported pairs are explained, not represented as arbitrary executable edges.
- Join scenarios explicitly identify all participating inputs and expected output.
- Ordered-chain side effects and downstream impact remain visible during review.

### Phase 4: Expanded verification assistance

- Broaden isolated mapping previews and expected-output comparisons to additional supported operations.
- Generate useful scenario-based test cases and diagnostics without automatically executing generated plugins or external deliveries.
- Distinguish schema validation, sample agreement, and production correctness; sample agreement is not a universal correctness claim.

## Validation strategy

- Test consent and actual serialized provider context, not only checkbox state.
- Test multi-sample inference, optional/null fields, empty arrays, nested/repeated records, conflicting types, and explicit join scenario pairing.
- Test proposal scope enforcement, stale-draft rejection, authority preservation, apply/undo, and unavailable-module handling.
- Test preview success, output differences, parse failures, unsupported operations, and resource limits.
- Build representative downloaded plugin projects and verify fail-closed stubs and approved fixture contents.
- Keep tests deterministic and avoid allowance-consuming live model requests unless explicitly authorized.

## Recommended delivery order

Start with Phase 1 for a single parsed source and renderer, including multi-sample shape evidence and supported mapping previews. Follow with downloadable plugin scaffolding, then additional pairs and broader verification.
