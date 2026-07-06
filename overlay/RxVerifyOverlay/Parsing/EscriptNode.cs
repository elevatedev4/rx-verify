using System.Collections.Generic;

namespace RxVerifyOverlay.Parsing;

/// <summary>
/// Thin, UIA-free representation of one node in the Escript tab's UIA
/// Tree (AutomationId <c>ux10Dot6Escript</c>). A node's Name is either a
/// container name ("Patient", "MedicationPrescribed", "NewRx", ...) or a
/// "Key: Value" leaf string, exactly as FlaUI reports each TreeItem's
/// <c>.Name</c> for that tree (confirmed against a real UIA dump — see
/// Uia/FieldMap.cs header). Children preserve the tree's nesting depth.
///
/// This type intentionally has ZERO dependency on FlaUI/UIA so
/// EscriptTreeParser (the thing that turns this into a PrescriptionRecord)
/// can be unit tested with synthetic in-memory trees, with no Windows/UIA
/// runtime involved. Uia/UiaTreeWalker.BuildEscriptTree() is the one thin
/// adapter that walks a live AutomationElement tree and produces this
/// struct.
/// </summary>
public sealed class EscriptNode
{
    public string Name { get; }
    public List<EscriptNode> Children { get; }

    public EscriptNode(string name, List<EscriptNode>? children = null)
    {
        Name = name;
        Children = children ?? new List<EscriptNode>();
    }

    /// <summary>Convenience for building synthetic trees in tests: EscriptNode.Container("Patient", child1, child2, ...).</summary>
    public static EscriptNode Container(string name, params EscriptNode[] children) =>
        new(name, new List<EscriptNode>(children));

    /// <summary>Convenience for building a synthetic "Key: Value" leaf in tests.</summary>
    public static EscriptNode Leaf(string key, string value) =>
        new($"{key}: {value}");
}
