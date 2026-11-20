using System.Text.Json.Serialization;

namespace Service.Models;

/// <summary>How a re-import relates to one piece of an already imported flow.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BizTalkUpdatePieceState>))]
public enum BizTalkUpdatePieceState
{
    /// <summary>The importer and the saved flow still agree on what was originally imported.</summary>
    Unchanged,

    /// <summary>The importer output is unchanged but the saved flow differs: a user edit that an update leaves alone unless it is selected to reset.</summary>
    KeptEdit,

    /// <summary>The saved flow already holds the new importer output.</summary>
    AlreadyCurrent,

    /// <summary>The importer now produces a piece the saved flow does not have.</summary>
    Add,

    /// <summary>The importer output changed and the saved piece was never edited.</summary>
    UpdateInPlace,

    /// <summary>The importer output changed and the saved piece was edited too; replacing it discards the edit.</summary>
    Conflict,

    /// <summary>The importer no longer produces a piece it once did. Reported only; an update never deletes it.</summary>
    Removed
}

[JsonConverter(typeof(JsonStringEnumConverter<BizTalkUpdateMode>))]
public enum BizTalkUpdateMode
{
    /// <summary>No piece needs importing.</summary>
    NothingToDo,

    /// <summary>The saved flow can be updated piece by piece.</summary>
    InPlace,

    /// <summary>The node structure differs, so the new import must be saved as a separate disabled flow for side-by-side review.</summary>
    NewDraft,

    /// <summary>The saved flow predates origin tracking, so edits cannot be told from importer output; import as a separate disabled flow.</summary>
    NoOrigin
}

public sealed record BizTalkUpdatePiece(string Key, string Kind, string Label, BizTalkUpdatePieceState State, bool Selectable, bool SelectedByDefault);

public sealed record BizTalkUpdatePlan(
    string FlowId,
    BizTalkUpdateMode Mode,
    string Reason,
    DateTimeOffset? ImportedUtc,
    IReadOnlyList<BizTalkUpdatePiece> Pieces,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<string> Notes);

/// <summary>Payload for updating an already imported flow from a fresh analysis.</summary>
public sealed record UpdateImportedBizTalkFlowRequest
{
    /// <summary>The same analysis inputs as the plan call. <see cref="CreateImportedBizTalkFlowRequest.NewFlowId"/> is required when the plan mode is NewDraft or NoOrigin.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    public required CreateImportedBizTalkFlowRequest Import { get; init; }

    /// <summary>Piece keys from the plan to replace; ignored for NewDraft and NoOrigin, which create a whole new flow.</summary>
    public IReadOnlyList<string> ReplacePieces { get; init; } = [];
}

public sealed record BizTalkUpdateResult(
    BizTalkUpdateMode Mode,
    FlowResponse Flow,
    IReadOnlyList<string> ReplacedPieces,
    IReadOnlyList<string> Notes);
