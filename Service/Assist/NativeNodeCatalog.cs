using Mulse.Modules.ControlFlow;
using Service.Models;

namespace Service.Assist;

/// <summary>
/// Plain-language facts for every kind of native node. A test fails when a kind is added to <see cref="NativeFlowNodeKind"/> without an entry here,
/// so the explain feature and any AI prompt built from it cannot silently drift from the product.
/// </summary>
public static class NativeNodeCatalog
{
    public const string EffectNone = "None";
    public const string EffectModule = "Depends on the module";
    public const string EffectSends = "Sends data out";
    public const string EffectPauses = "Pauses the flow";
    public const string EffectStops = "Stops the flow";
    public const string EffectBlocked = "Cannot run";

    private static readonly AssistNodeKindInfo[] Entries =
    [
        Info(NativeFlowNodeKind.Sequence, "Sequence", "Runs its steps one after another.", EffectNone, "None of its own."),
        Info(NativeFlowNodeKind.If, "If", "Checks its conditions. When they hold it runs its steps, otherwise it runs the steps under Otherwise.", EffectNone, "None of its own; the steps it chooses may have effects.", "Conditions"),
        Info(NativeFlowNodeKind.ForEach, "For each", "Runs its steps once for each item in the current batch of messages, up to a limit.", EffectNone, "None of its own; the steps inside run once per item, so a send inside it sends once per item.", "MaxIterations"),
        Info(NativeFlowNodeKind.While, "While", "Repeats its steps while its condition holds, up to a limit. Exceeding the limit stops the flow with an error.", EffectNone, "None of its own; the steps inside repeat.", "Conditions", "MaxIterations"),
        Info(NativeFlowNodeKind.Augment, "Augment", "Runs a module that adds to or reshapes the current data, for example a lookup or a combine.", EffectModule, "Set by the module. Read its description before assuming it only reads.", "Module", "Settings"),
        Info(NativeFlowNodeKind.Render, "Render", "Runs a module that turns the current data into the output format, for example XML, a flat file or HL7.", EffectModule, "Set by the module. Rendering produces data for a later step; sending is a Deliver step.", "Module", "Settings"),
        Info(NativeFlowNodeKind.Blocked, "Not translated", "Something the importer could not translate. It stays visible and the flow cannot run until it is replaced.", EffectBlocked, "The flow is held back; nothing runs past it.", "BlockReason"),
        Info(NativeFlowNodeKind.Wait, "Wait", "Pauses for a fixed time or a calculated number of seconds. Under one second it pauses in place. A second or more parks the flow until its next run.", EffectPauses, "Nothing is sent. The flow instance is kept while it waits.", "DurationSeconds", "DurationMilliseconds", "DurationExpression"),
        Info(NativeFlowNodeKind.Receive, "Receive", "Waits for a message on a channel, matching it by correlation keys. It may time out.", EffectPauses, "Takes a message that arrives for this flow instance; a message that does not match is left alone.", "Channel", "CorrelationKeys", "MessageName"),
        Info(NativeFlowNodeKind.SaveMessage, "Save message", "Stores the current data under a name so a later step can use it.", EffectNone, "Kept in the flow's own state only.", "MessageName"),
        Info(NativeFlowNodeKind.UseMessage, "Use message", "Makes a previously saved message the current data.", EffectNone, "None.", "MessageName"),
        Info(NativeFlowNodeKind.Reply, "Reply", "Returns a saved message as the answer to the caller that started this run.", EffectSends, "The caller receives the message. Nothing is sent to a configured destination.", "MessageName"),
        Info(NativeFlowNodeKind.Deliver, "Deliver", "Sends the current data (or a named message) to a destination through a deliver module.", EffectSends, "Writes to the destination the module is set up for. Retry settings decide whether a failed send is tried again.", "Module", "Settings", "Retry", "MessageName"),
        Info(NativeFlowNodeKind.DeclareVariable, "Declare variable", "Creates a named value of a stated type, optionally with a starting value.", EffectNone, "None.", "Variable", "VariableType", "Expression"),
        Info(NativeFlowNodeKind.SetVariable, "Set variable", "Changes a named value using an expression.", EffectNone, "None.", "Variable", "Expression"),
        Info(NativeFlowNodeKind.CreateXmlMessage, "Create XML message", "Starts a new named XML message.", EffectNone, "Kept in the flow's own state only.", "MessageName"),
        Info(NativeFlowNodeKind.SetMessageValue, "Set message value", "Writes a value into a named field of a message.", EffectNone, "Changes the message in the flow's own state only.", "MessageName", "Target", "Expression"),
        Info(NativeFlowNodeKind.CreateXmlRoot, "Create XML root", "Creates the root element of a named message in a given namespace.", EffectNone, "Kept in the flow's own state only.", "MessageName", "NamespaceExpression"),
        Info(NativeFlowNodeKind.AppendXmlMessage, "Append XML message", "Adds the content of one message to another.", EffectNone, "Changes the target message in the flow's own state only.", "MessageName", "SourceMessage"),
        Info(NativeFlowNodeKind.ApplyXmlRules, "Apply rules", "Runs a business rules policy over a named XML message. By default a message that matches no rule is left unchanged.", EffectNone, "Changes the message in the flow's own state when a rule matches.", "MessageName", "PolicyJson", "StopWhenNoRuleMatches"),
        Info(NativeFlowNodeKind.Terminate, "Terminate", "Stops the flow at this point with a message. It is not caught by Try.", EffectStops, "The run ends in error with the stated message; steps after it do not run.", "TerminateMessage"),
        Info(NativeFlowNodeKind.Try, "Try", "Runs its steps. If one fails, runs the steps under Otherwise as the handler. Stops such as Terminate, waits and pending sends of unknown outcome are not caught.", EffectNone, "None of its own; the handler's steps may have effects.", "ExceptionMessageVariable", "ExceptionDetailsVariable"),
        Info(NativeFlowNodeKind.InvokeTransform, "Run transform", "Runs a small installed value function with calculated inputs and stores the text result in a variable.", EffectNone, "None; the function is pure and only returns a value.", "Module", "TransformInputs", "Variable"),
        Info(NativeFlowNodeKind.Listen, "Listen", "Waits on several branches at once and continues with whichever happens first: a message arriving or a time-out.", EffectPauses, "Takes the message of the branch that wins; the other branches are dropped.", "Children", "Channel", "CorrelationKeys")
    ];

    private static readonly Dictionary<NativeFlowNodeKind, AssistNodeKindInfo> ByKind = Entries.ToDictionary(entry => Enum.Parse<NativeFlowNodeKind>(entry.Kind));

    public static IReadOnlyList<AssistNodeKindInfo> All => Entries;

    public static AssistNodeKindInfo For(NativeFlowNodeKind kind) => ByKind.TryGetValue(kind, out var info)
        ? info
        : new(kind.ToString(), kind.ToString(), "No description is available for this kind of step.", EffectModule, "Unknown.", []);

    private static AssistNodeKindInfo Info(NativeFlowNodeKind kind, string title, string summary, string effect, string sideEffects, params string[] keySettings)
        => new(kind.ToString(), title, summary, effect, sideEffects, keySettings);
}
