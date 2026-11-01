using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private IEditAction ExternalizeProxyInlineGeometry(IReadOnlyList<CompiledElement> references)
    {
        using var transaction = new ImportTransaction(this, null);
        var edits = new List<IEditAction>();
        try
        {
            foreach (var reference in references.Where(pointer => pointer.Event == 2 && pointer.Values.Count() > 1))
            {
                var values = reference.Values.ToArray();
                if (Graph.Kind(reference) != 173 || values.Length != 2 || values[1].ObjectId == 0)
                    throw new InvalidDataException("Unknown inline geometry ownership layout.");
                var definition = values[1]; var resource = Graph.Owner(reference); var originalChildren = reference.Children.ToArray();
                var container = Graph.Nodes.Select(node => Graph.Field(node, "m_Objects")).OfType<CompiledElement>()
                    .FirstOrDefault(node => ReferenceEquals(Graph.Owner(node), resource) && CurrentItems(node).Any())
                    ?? throw new InvalidDataException("Inline geometry owner has no native registration container.");
                var prototype = CurrentItems(container).First(item => Descendants(item).Any(node => Graph.Kind(node) == 173 && node.Values.Count() > 1));
                // Retain the original native local pointer ID: other references in this resource may
                // name it. Only ownership moves; its GUID, class allocation and ToC stay unchanged.
                reference.Children.Remove(definition); Graph.SetSubtreeActive(resource, definition, false);
                var registration = OwnershipShell(resource, prototype, definition, Graph.Data(values[0]).ToArray());
                SetItems(container, CurrentItems(container).Append(registration).ToArray()); bool active = true;
                edits.Add(new ProxyGeometryAction(
                    () =>
                    {
                        if (active) return;
                        reference.Children.Remove(definition); Graph.SetSubtreeActive(resource, definition, false);
                        Graph.SetSubtreeActive(resource, registration, true);
                        SetItems(container, CurrentItems(container).Append(registration).ToArray()); active = true;
                    },
                    () =>
                    {
                        if (!active) return;
                        SetItems(container, CurrentItems(container).Where(item => !ReferenceEquals(item, registration)).ToArray());
                        Graph.SetSubtreeActive(resource, registration, false);
                        reference.Children.Clear(); reference.Children.AddRange(originalChildren);
                        Graph.SetSubtreeActive(resource, reference, true); active = false;
                    }));
            }
            transaction.Commit();
            return new ProxyGeometryAction(() => { foreach (var edit in edits) edit.Redo(); }, () => { foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo(); });
        }
        catch { foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo(); throw; }
    }
}
