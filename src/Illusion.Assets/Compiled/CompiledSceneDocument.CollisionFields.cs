using System.Buffers.Binary;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private IEditAction EditCollisionField(CompiledCollisionNode source, string id, object? value)
    {
        if ((id == "m_Radius" && value is float radius && (!float.IsFinite(radius) || radius <= 0))
            || (id == "m_Height" && value is float height && (!float.IsFinite(height) || height < 0)))
            throw new ArgumentOutOfRangeException(nameof(value), "Capsule radius must be positive and segment height nonnegative.");
        using var transaction = new ImportTransaction(this, null);
        var copies = new GeometryCopy(this, destination: Graph.Owner(source.Frame.Element));
        try
        {
            var shape = copies.CloneDefinition(source.Shape, followDependencies: false);
            copies.Finish();
            var property = new CompiledProperties(Graph, shape, source.TypeName).GetPropertyGroups()
                .SelectMany(group => group.Properties).First(property => property.Id == id);
            if (property.Set is null) throw new NotSupportedException("This collision field is read-only.");
            property.Set(value);
            SetNumber(shape, "m_Hash", shape.ObjectId);
            byte[] before = Graph.Data(source.Reference).ToArray(), after = (byte[])before.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(after, shape.ObjectId);
            Graph.Owner(source.Reference).Replace(source.Reference, after);
            var edit = new CollisionFieldEdit(this, copies, source.Reference, before, after);
            transaction.Commit();
            return edit;
        }
        catch { copies.Detach(); throw; }
    }

    private sealed class CollisionFieldEdit(CompiledSceneDocument document, GeometryCopy copies,
        CompiledElement reference, byte[] before, byte[] after) : IEditAction
    {
        private bool _applied = true;
        public void Undo()
        {
            if (!_applied) return;
            document.Graph.Owner(reference).Replace(reference, before); copies.Detach(); _applied = false;
        }
        public void Redo()
        {
            if (_applied) return;
            copies.Reattach(); document.Graph.Owner(reference).Replace(reference, after); _applied = true;
        }
    }
}
