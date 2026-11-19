using Service.Models;

namespace Service.Assist;

/// <summary>Source-system terms shown first when a user asks what something in an imported flow means. General concepts only; nothing here is specific to one application.</summary>
public static class AssistGlossary
{
    private static readonly AssistGlossaryEntry[] Entries =
    [
        new("Orchestration", "A BizTalk workflow drawn in the Orchestration Designer and compiled from an .odx file. It receives messages, makes decisions, calls maps and rules, and sends messages.", "A native flow: an ordered tree of steps. Each BizTalk shape becomes a step, and a shape that cannot be translated becomes a visible Not translated step.", ["ODX", "orchestration shape"]),
        new("MessageBox", "BizTalk's central SQL Server database. Receive ports publish messages into it, and send ports and orchestrations subscribe to them.", "There is no central box. A flow is started by its own trigger, and a durable flow keeps its own state while it waits.", ["BizTalkMsgBoxDb", "publish subscribe"]),
        new("Receive port", "The entry point for messages, with one or more receive locations that name the adapter and address, and an optional receive pipeline and map.", "The trigger and fetch steps of a flow. Addresses are settings you edit in the UI.", ["receive location"]),
        new("Send port", "A configured destination: adapter and address, a send pipeline, an optional map, filters that pick which messages it gets, and retry settings.", "A Deliver step with its module settings and retry. Filters and subscriptions become the conditions that lead to that step.", ["send port group", "subscription", "filter"]),
        new("Send pipeline", "The stages (encode, assemble and so on) that turn a message into the bytes sent by a send port. Components such as an XML or flat-file assembler do the work.", "Render steps for known components. A component with no known equivalent leaves the send blocked, and the import report names it.", ["transmit pipeline", "XMLTransmit", "pipeline component", "assembler"]),
        new("Receive pipeline", "The stages (decode, disassemble, validate) that turn incoming bytes into messages.", "Parse steps for known components. Unknown components stay visible and need a decision.", ["disassembler"]),
        new("Map", "A BizTalk transformation (.btm) between two schemas, built from links and functoids and compiled to XSLT.", "A native mapping with the same links where they can be converted. Functoids that cannot be converted are listed as stubs you complete.", ["BTM", "XSLT", "transform shape"]),
        new("Functoid", "A building block on a map. Simple ones copy or calculate values. A scripting functoid holds your own code.", "Known functoids become mapping operations. A scripting functoid becomes a custom function stub that fails closed until you give it a formula in the UI.", ["scripting functoid"]),
        new("Schema", "An XSD that describes a message, often with promoted and distinguished fields.", "Used for mapping and for editing message values by path.", ["XSD", "distinguished field"]),
        new("Promoted property", "A message field copied into the message context so it can be used for routing, filtering and correlation.", "A metadata key on the message. Correlation and routing read these keys, and you can edit the key names.", ["context property", "property schema"]),
        new("Correlation set", "A set of promoted properties that ties a later message to the orchestration instance that is waiting for it.", "Correlation keys on Receive and Listen steps, matched against message metadata.", ["correlation", "following correlation"]),
        new("Convoy", "Several related messages handled by one orchestration instance through correlation.", "A durable flow with a Receive step that waits for the matching message. Convoys that follow a correlation set are only partly supported.", ["sequential convoy", "parallel convoy"]),
        new("Dehydration", "BizTalk saves an orchestration that is waiting and removes it from memory until it is needed again.", "A durable flow is kept in its own state while it waits. A Wait of a second or more parks it until the next run.", ["rehydration"]),
        new("Delay shape", "A pause in an orchestration for a fixed or calculated time.", "A Wait step.", ["Delay"]),
        new("Listen shape", "Waits on several branches and continues with the first that has a message or whose delay expires.", "A Listen step.", ["Listen", "ListenBranch"]),
        new("Terminate shape", "Stops an orchestration instance with an error message.", "A Terminate step. It is not caught by Try.", ["Terminate"]),
        new("Scope", "A block of shapes that can have its own exception handlers and a transaction type.", "A Sequence or Try step. Transaction behavior is noted as not enforced.", ["atomic transaction", "long running transaction", "exception handler", "compensation"]),
        new("Business Rules Engine", "BizTalk's rule service. A policy is a set of rules saved under a name and a version, and an orchestration calls it with Call Rules.", "An Apply rules step with a policy copied into the flow. Which version is deployed is not inferred, so you choose the version to follow.", ["BRE", "policy", "Call Rules", "vocabulary"]),
        new("Binding file", "An XML export of ports, addresses and subscriptions for a deployed application.", "Evidence the importer reads to fill in destination addresses and settings.", ["bindings", "BindingInfo"]),
        new("Host and host instance", "A logical group and the Windows service process that runs orchestrations, ports or adapters.", "No equivalent is needed. Mulse runs flows in its own service.", ["BTSNTSvc", "host"]),
        new("Send port retry", "How many times BizTalk tries a failed send and how long it waits between tries.", "The Retry setting on a Deliver step. By default only a failure that is known not to have sent anything is retried; retrying a send of unknown outcome is an explicit opt-in that can duplicate data.", ["retry count", "retry interval"]),
        new("Suspended message", "A message BizTalk could not process and keeps for an operator to resume or terminate.", "Failed or held work is shown for the operator to decide. Mulse never resends on its own.", ["suspended instance", "failed message routing"])
    ];

    public static IReadOnlyList<AssistGlossaryEntry> All => Entries;

    /// <summary>Entries whose term or alias contains the text, ignoring case. An empty search returns everything.</summary>
    public static IReadOnlyList<AssistGlossaryEntry> Find(string? search)
    {
        var text = search?.Trim();
        if (string.IsNullOrEmpty(text)) return Entries;
        return [.. Entries.Where(entry => entry.Term.Contains(text, StringComparison.OrdinalIgnoreCase)
            || entry.Aliases.Any(alias => alias.Contains(text, StringComparison.OrdinalIgnoreCase)))];
    }
}
