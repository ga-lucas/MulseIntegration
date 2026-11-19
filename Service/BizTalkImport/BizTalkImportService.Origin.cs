using Service.BizTalkImport.Origin;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    /// <summary>
    /// Records fingerprints of what the importer produced for this candidate (hashes only, no content). A later re-import compares them with the saved flow to tell
    /// importer changes from user edits. Deterministic: the import time is stamped by the API when the flow is saved, not here.
    /// </summary>
    internal static BizTalkFlowCandidateResponse AttachOrigin(BizTalkFlowCandidateResponse candidate)
    {
        var migration = candidate.DraftFlow.Migration;
        if (migration is null) return candidate;
        var pieces = BizTalkOriginPieces.Read(BizTalkUpdatePlanner.DraftNativeFlowJson(candidate.DraftFlow), candidate.StoredConfigValues).Hashes;
        return candidate with { DraftFlow = candidate.DraftFlow with { Migration = migration with { Origin = new MigrationOrigin(pieces) } } };
    }
}
