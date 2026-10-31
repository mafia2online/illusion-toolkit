using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public sealed partial class CompiledFrameNode
    {
        internal string GeometryArchivePath => _document.SourceArchive.FullName;
    }
    private readonly Dictionary<(CompiledFrameNode Frame, CompiledElement Visual), CompiledVisualSource> _visualGeometry = [];

    private CompiledVisualSource VisualGeometry(CompiledFrameNode frame, CompiledElement visual, CompiledMeshDecoder decoder, int index)
    {
        var key = (frame, visual);
        if (!_visualGeometry.TryGetValue(key, out var source))
        {
            source = new CompiledVisualSource(frame, index);
            source.GeometryBinding = new CompiledMeshBinding(Graph, visual, source, frame.Name,
                level => DecodeVisual(frame, visual, decoder, level),
                () => GeometryBind(frame, visual));
            _visualGeometry.Add(key, source);
        }
        return source;
    }

    private Matrix4x4? GeometryBind(CompiledFrameNode frame, CompiledElement visual)
    {
        if (Graph.Items(Graph.Field(visual, "m_SkeletonBoneIDsPerLOD")).Count != 0)
        {
            foreach (var component in Components(frame.Element))
            {
                var immutable = Graph.Resolve(Graph.Field(component, "m_ImmutableData"));
                if (Graph.Field(immutable, "m_BoneInvMatrices") is { DataSize: >= 52 } matrices)
                {
                    var bind = ReadMatrix(Graph.Data(matrices).Slice(4, 48));
                    return CompiledMeshBinding.IsRigidBind(bind) ? bind : null;
                }
            }
            return null;
        }
        return CompiledMeshBinding.TryImmutableRigidBind(Graph, frame.Element, visual, out var rigid) ? rigid : null;
    }

    private sealed partial class CompiledVisualSource(CompiledFrameNode frame, int index) : ICompiledMeshSource
    {
        public string SceneIdentity => $"{frame.SceneIdentity}:visual:{index}";
        public IFrameNode CanonicalFrame => frame;
        public CompiledMeshBinding? GeometryBinding { get; set; }
        public Matrix4x4 LocalTransform { get => frame.LocalTransform; set => frame.LocalTransform = value; }
        public Matrix4x4 WorldTransform => frame.WorldTransform;
        public Matrix4x4 ParentWorldTransform => frame.ParentWorldTransform;
        public IFrameNode? Parent => frame.Parent;
        public bool IsOnNameTable => frame.IsOnNameTable;
        public int NameTableFlags => frame.NameTableFlags;
    }
}
