using Mulse.Modules.Orchestration;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private const int MaximumTransformInputs = 32;

    private sealed record TransformShape(string ClassName, IReadOnlyList<string> Inputs, string Output);

    /// <summary>
    /// Reads an orchestration Transform shape: a map class, ordered input message parts and exactly one output message part.
    /// A part that names a message part (rather than a whole message) carries part-level semantics and is not read.
    /// </summary>
    private static TransformShape? ReadTransformShape(OdxNode node, IReadOnlySet<string> messages, out string problem)
    {
        problem = "";
        var className = node.Properties.GetValueOrDefault("ClassName", "").Trim();
        if (className.Length == 0) { problem = "Transform shape names no map class."; return null; }
        var inputs = new List<string>();
        var outputs = new List<string>();
        foreach (var child in node.Children)
        {
            if (child.ShapeType != "MessagePartRef" || child.ParentLink is not ("Transform_InputMessagePartRef" or "Transform_OutputMessagePartRef") || child.Children.Count != 0)
            { problem = "Transform shape contains something other than input and output message references."; return null; }
            var message = child.Properties.GetValueOrDefault("MessageRef", "").Trim();
            if (child.Properties.TryGetValue("PartRef", out var part) && part.Trim().Length > 0)
            { problem = $"Transform reads or writes part '{part}' of message '{message}'; part-level map inputs and outputs are not translated."; return null; }
            if (message.Length == 0 || !messages.Contains(message))
            { problem = $"Transform message reference '{message}' is not an exact declared service message."; return null; }
            (child.ParentLink == "Transform_InputMessagePartRef" ? inputs : outputs).Add(message);
        }
        if (inputs.Count is 0 or > MaximumTransformInputs || outputs.Count != 1)
        { problem = $"Transform needs 1-{MaximumTransformInputs} input messages and exactly one output message."; return null; }
        if (inputs.Contains(outputs[0], StringComparer.Ordinal))
        { problem = "Transform output message is also one of its inputs; in-place replacement is not translated."; return null; }
        if (inputs.Distinct(StringComparer.Ordinal).Count() != inputs.Count)
        { problem = "Transform lists the same input message more than once; repeated inputs are not translated."; return null; }
        return new(className, inputs, outputs[0]);
    }

    /// <summary>Map class names of every static Transform shape in the given orchestration files; unreadable files contribute nothing.</summary>
    private static IReadOnlyList<PolicyMapNameLiteral> ReadTransformShapeMapNames(IEnumerable<string> orchestrationPaths)
    {
        var literals = new List<PolicyMapNameLiteral>();
        foreach (var path in orchestrationPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            OdxProgram program;
            try { program = OdxProgramReader.ReadFile(path); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
            { continue; }
            foreach (var shape in FlattenNativeSource(program.Roots).Where(static node => node.ShapeType == "Transform"))
                if (shape.Properties.GetValueOrDefault("ClassName", "").Trim() is { Length: > 0 } className)
                    literals.Add(new(Path.GetFileName(path), "-", "Transform shape " + shape.Name, className));
        }
        return literals;
    }
}
