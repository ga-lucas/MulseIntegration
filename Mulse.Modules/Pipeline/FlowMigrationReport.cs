using System.Text.Json.Serialization;

namespace Mulse.Modules.Pipeline;

[JsonConverter(typeof(JsonStringEnumConverter<MigrationTranslationStatus>))]
public enum MigrationTranslationStatus
{
    FullyTranslated,
    ReviewRequired,
    StubGenerated,
    Unsupported,
    ConfigurationRequired
}

[JsonConverter(typeof(JsonStringEnumConverter<MigrationDiagnosticSeverity>))]
public enum MigrationDiagnosticSeverity
{
    Information,
    Review,
    Blocking
}

/// <summary>Import-time evidence, not an execution approval or a live validation result.</summary>
public sealed record FlowMigrationReport(
    string SourceSystem,
    string SourceProjectPath,
    string? SourceOrchestration,
    IReadOnlyList<MigrationArtifactReport> Artifacts,
    IReadOnlyList<MigrationDiagnostic> Diagnostics,
    IReadOnlyList<string> Warnings)
{
    public int Version { get; init; } = 1;

    public MigrationMapInventory? MapInventory { get; init; }

    /// <summary>Fingerprints of what the importer produced, so a later re-import can tell importer changes from user edits. Null on flows imported before origin tracking existed.</summary>
    public MigrationOrigin? Origin { get; init; }
}

/// <summary>
/// What an import produced, as hashes only (never content). Keys name independently updatable pieces of the imported flow: <c>structure</c>, <c>flow</c>,
/// <c>node:&lt;id&gt;</c>, <c>choice:&lt;nodeId&gt;:&lt;value&gt;</c>, <c>shared-choice:&lt;value&gt;</c> and <c>config:&lt;name&gt;</c>.
/// </summary>
public sealed record MigrationOrigin(IReadOnlyDictionary<string, string> Pieces)
{
    /// <summary>When the flow was last created or updated from an import; stamped by the API, not by analysis, so analysis stays deterministic.</summary>
    public DateTimeOffset? ImportedUtc { get; init; }
}

public sealed record MigrationArtifactReport(
    string Kind,
    string SourcePath,
    MigrationTranslationStatus Status,
    IReadOnlyList<MigrationDiagnostic> Diagnostics)
{
    public OrchestrationMigrationInventory? Orchestration { get; init; }
    public PipelineMigrationInventory? Pipeline { get; init; }
}

public sealed record OrchestrationMigrationInventory(
    IReadOnlyDictionary<string, int> ShapeCounts,
    IReadOnlyList<OrchestrationShapeReport> Shapes);

public sealed record OrchestrationShapeReport(
    int Id,
    int? ParentId,
    int SourceLine,
    string ShapeType,
    string Name,
    IReadOnlyDictionary<string, string> References);

public sealed record MigrationDiagnostic(
    string Code,
    MigrationDiagnosticSeverity Severity,
    string SourcePath,
    string Message,
    string Remediation);
