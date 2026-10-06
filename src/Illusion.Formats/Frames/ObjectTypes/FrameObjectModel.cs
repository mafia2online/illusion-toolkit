using System.Numerics;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Frames.ObjectTypes;

public class FrameObjectModel : FrameObjectSingleMesh
{
    private FrameSkeleton? _skeleton;
    private FrameBlendInfo? _blendInfo;
    private FrameSkeletonHierarchy? _hierarchy;

    int blendInfoIndex;
    int skeletonIndex;
    int skeletonHierarchyIndex;
    Matrix4x4[] restTransform = null!;
    Matrix4x4 unkTransform;
    AttachmentReference[] attachmentReferences = null!;
    uint unkFlags;
    int physSplitSize;
    int hitBoxSize;
    short nPhysSplits;
    WeightedByMeshSplit[] blendMeshSplits = null!;
    HitBoxInfo[] hitBoxInfo = null!;
    public int BlendInfoIndex
    {
        get { return blendInfoIndex; }
        set { blendInfoIndex = value; }
    }
    public int SkeletonIndex
    {
        get { return skeletonIndex; }
        set { skeletonIndex = value; }
    }
    public int SkeletonHierarchyIndex
    {
        get { return skeletonHierarchyIndex; }
        set { skeletonHierarchyIndex = value; }
    }
    public WeightedByMeshSplit[] BlendMeshSplits
    {
        get { return blendMeshSplits; }
        set { blendMeshSplits = value; }
    }
    public Matrix4x4[] RestTransform
    {
        get { return restTransform; }
        set { restTransform = value; }
    }
    public Matrix4x4 UnkTransform
    {
        get { return unkTransform; }
        set { unkTransform = value; }
    }
    public HitBoxInfo[] HitBoxes
    {
        get { return hitBoxInfo; }
        set { hitBoxInfo = value; }
    }
    public AttachmentReference[] AttachmentReferences
    {
        get { return attachmentReferences; }
        set { attachmentReferences = value; }
    }
    public uint UnkFlags
    {
        get { return unkFlags; }
        set { unkFlags = value; }
    }

    /// <summary>
    /// Hangs <paramref name="frame"/> off one of this model's joints, so it moves with that bone. Both halves
    /// are done here because they have to agree: the saved reference list, and the runtime link the frame
    /// carries back to its joint. <see cref="AttachmentReference.AttachmentIndex"/> is not set — it is
    /// recomputed from the resolved object when the resource is written.
    /// </summary>
    public void AttachToJoint(FrameObjectBase frame, byte joint)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var reference = new AttachmentReference { JointIndex = joint, Attachment = frame };
        attachmentReferences = [.. attachmentReferences ?? [], reference];
        frame.SetAttachedJoint(this, joint);
    }

    /// <summary>Undoes <see cref="AttachToJoint"/>. Silent when the frame was not attached.</summary>
    public void DetachFromJoints(FrameObjectBase frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        attachmentReferences = [.. (attachmentReferences ?? [])
            .Where(r => !ReferenceEquals(r.Attachment, frame))];
        frame.ClearAttachedJoint();
    }
    public FrameSkeleton Skeleton
    {
        get { return GetSkeletonObject(); }
        set { _skeleton = value; }
    }
    public FrameBlendInfo BlendInfo
    {
        get { return GetBlendInfoObject(); }
        set { _blendInfo = value; }
    }
    public FrameSkeletonHierarchy SkeletonHierarchy
    {
        get { return GetSkeletonHierarchyObject(); }
        set { _hierarchy = value; }
    }

    /// <summary>The three private on-disk counters, exposed for the native-boundary mapper
    /// (they are read/written verbatim; nPhysSplits is only stored when physSplitSize > 0).</summary>
    internal (int PhysSplitSize, int HitBoxSize, short NPhysSplits) SplitCounters
    {
        get { return (physSplitSize, hitBoxSize, nPhysSplits); }
        set { physSplitSize = value.PhysSplitSize; hitBoxSize = value.HitBoxSize; nPhysSplits = value.NPhysSplits; }
    }

    /// <summary>
    /// The byte size the split block serializes to, computed from the table as it stands. The counter is
    /// stored in the file and written back verbatim, so a rebuilt table has to bring it along — a size that
    /// no longer matches the bytes after it desynchronizes everything the game reads next, and the model
    /// simply does not appear.
    /// <para>
    /// The layout is a count, then per split a blend index and a piece count, then per piece a burst count,
    /// then per burst a material index and a face-range count, then four bytes per range. Verified against
    /// every shipped car by <c>--probe-skinning</c>.
    /// </para>
    /// </summary>
    /// <summary>The split-block size as the FILE holds it — for checking a rebuild against
    /// <see cref="ComputeSplitBlockSize"/>.</summary>
    public int SplitBlockSizeStored => physSplitSize;

    /// <summary>The three stored counters as they stand — read to remember a split table exactly as it was,
    /// written to put it back that way (an undo). Anything that BUILDS a table calls
    /// <see cref="RecomputeSplitCounters"/> instead.</summary>
    public (int PhysSplitSize, int HitBoxSize, short NPhysSplits) StoredSplitCounters
    {
        get { return SplitCounters; }
        set { SplitCounters = value; }
    }

    public int ComputeSplitBlockSize()
    {
        if (blendMeshSplits is not { Length: > 0 }) return 0;

        int size = sizeof(short); // the split count itself
        foreach (WeightedByMeshSplit split in blendMeshSplits)
        {
            size += sizeof(ushort) + sizeof(ushort); // blend index + piece count
            foreach (BlendMeshSplitInfo piece in split.Data ?? [])
            {
                size += sizeof(short); // burst count
                foreach (MiniMaterialBurst burst in piece.Data ?? [])
                {
                    size += sizeof(ushort) + sizeof(ushort); // material index + range count
                    size += (burst.Data?.Length ?? 0) * (sizeof(ushort) + sizeof(ushort));
                }
            }
        }
        return size;
    }

    /// <summary>
    /// Brings the three stored counters back in step with the tables they describe. Call after rebuilding
    /// the splits — the hit-box counter is sixteen bytes per PIECE (the reader takes the piece count as the
    /// hit-box count), and the split count is simply how many there are.
    /// </summary>
    public void RecomputeSplitCounters()
    {
        int pieces = 0;
        foreach (WeightedByMeshSplit split in blendMeshSplits ?? [])
        {
            pieces += split.Data?.Length ?? 0;
        }

        physSplitSize = ComputeSplitBlockSize();
        hitBoxSize = pieces * 16;
        nPhysSplits = (short)(blendMeshSplits?.Length ?? 0);
    }

    public FrameObjectModel(FrameResource OwningResource) : base(OwningResource) { }

    public FrameObjectModel(FrameObjectSingleMesh other) : base(other)
    {
        restTransform = new Matrix4x4[0];
        attachmentReferences = new AttachmentReference[0];
        blendMeshSplits = new WeightedByMeshSplit[0];
        hitBoxInfo = new HitBoxInfo[0];
    }

    public FrameObjectModel(FrameObjectModel other) : base(other)
    {
        blendInfoIndex = other.blendInfoIndex;
        skeletonIndex = other.skeletonIndex;
        skeletonHierarchyIndex = other.skeletonHierarchyIndex;
        _skeleton = other._skeleton;
        _blendInfo = other._blendInfo;

        restTransform = new Matrix4x4[_skeleton!.NumBones[0]];
        for (int i = 0; i != restTransform.Length; i++)
        {
            restTransform[i] = MatrixExtensions.CopyFrom(other.restTransform[i]);
        }

        unkTransform = MatrixExtensions.CopyFrom(other.unkTransform);

        attachmentReferences = new AttachmentReference[other.attachmentReferences.Length];
        for (int i = 0; i != attachmentReferences.Length; i++)
        {
            attachmentReferences[i] = new AttachmentReference(other.attachmentReferences[i]);
        }

        unkFlags = other.unkFlags;
        physSplitSize = other.physSplitSize;
        hitBoxSize = other.hitBoxSize;
        nPhysSplits = other.nPhysSplits;

        blendMeshSplits = new WeightedByMeshSplit[nPhysSplits];
        for (int i = 0; i != blendMeshSplits.Length; i++)
        {
            blendMeshSplits[i] = new WeightedByMeshSplit(other.blendMeshSplits[i]);
        }

        hitBoxInfo = new HitBoxInfo[other.hitBoxInfo.Length];
        for (int i = 0; i != hitBoxInfo.Length; i++)
        {
            hitBoxInfo[i] = new HitBoxInfo(other.hitBoxInfo[i]);
        }
    }

    protected FrameBlendInfo ConstructBlendInfoObject()
    {
        _blendInfo = OwningResource.ConstructFrameAssetOfType<FrameBlendInfo>();
        AddRef(FrameEntryRefTypes.BlendInfo, _blendInfo.RefID);
        return _blendInfo;
    }
    protected FrameSkeletonHierarchy ConstructSkeletonHierarchyObject()
    {
        SkeletonHierarchy = OwningResource.ConstructFrameAssetOfType<FrameSkeletonHierarchy>();
        AddRef(FrameEntryRefTypes.SkeletonHierarchy, SkeletonHierarchy.RefID);
        return SkeletonHierarchy;
    }

    protected FrameSkeleton ConstructSkeletonObject()
    {
        _skeleton = OwningResource.ConstructFrameAssetOfType<FrameSkeleton>();
        AddRef(FrameEntryRefTypes.Skeleton, _skeleton.RefID);
        return _skeleton;
    }

    public FrameBlendInfo GetBlendInfoObject()
    {
        if (_blendInfo == null)
        {
            return ConstructBlendInfoObject();
        }

        return _blendInfo;
    }

    public FrameSkeletonHierarchy GetSkeletonHierarchyObject()
    {
        if (_hierarchy == null)
        {
            return ConstructSkeletonHierarchyObject();
        }

        return _hierarchy;
    }

    public FrameSkeleton GetSkeletonObject()
    {
        if (_skeleton == null)
        {
            return ConstructSkeletonObject();
        }

        return _skeleton;
    }

    /// <summary>
    /// Where one of this model's joints stands in the world: its rest transform carried through the model's
    /// own placement, composed the same way <see cref="FrameObjectBase.SetWorldTransform"/> composes a child
    /// with its parent. Out-of-range joints fall back to the model's own transform, so a mis-indexed
    /// attachment lands on the object rather than at the origin.
    /// <para>
    /// The rest transforms are read as MODEL space, which is the reading <c>--probe-cars</c> settled on.
    /// </para>
    /// </summary>
    public Matrix4x4 GetJointWorldTransform(int joint)
    {
        Matrix4x4 model = WorldTransform;
        if (restTransform == null || joint < 0 || joint >= restTransform.Length) return model;

        MatrixExtensions.TryDecomposeRS(restTransform[joint], out Vector3 scale, out Quaternion rotation, out Vector3 position);
        MatrixExtensions.TryDecomposeRS(model, out _, out Quaternion modelRotation, out _);
        return MatrixExtensions.SetMatrix(
            modelRotation * rotation, scale, Vector3Extensions.TransformCoordinate(position, model));
    }

    /// <summary>
    /// Where something given in one of this model's joint spaces ends up in the world. This is the same
    /// composition <see cref="FrameObjectBase.SetWorldTransform"/> uses to place an attached frame — rotation
    /// through the joint's rotation, position through the whole joint matrix — exposed for callers that have
    /// to place something which is not a frame at all: a car's collision volumes live in the PREFAB, in the
    /// space of the bone their deformable part is, and only a few of them have a stub frame to borrow.
    /// </summary>
    public Matrix4x4 PlaceOnJoint(Matrix4x4 local, int joint)
    {
        Matrix4x4 parent = GetJointWorldTransform(joint);
        MatrixExtensions.TryDecomposeRS(local, out Vector3 scale, out Quaternion rotation, out Vector3 position);
        MatrixExtensions.TryDecomposeRS(parent, out _, out Quaternion parentRotation, out _);
        return MatrixExtensions.SetMatrix(
            parentRotation * rotation, scale, Vector3Extensions.TransformCoordinate(position, parent));
    }

    public override string ToString()
    {
        return string.Format("{0}", Name.ToString());
    }

    public class AttachmentReference
    {
        int attachmentIndex;
        byte jointIndex;

        //not saved
        string jointName = null!;
        FrameObjectBase? attachment;
        public int AttachmentIndex
        {
            get { return attachmentIndex; }
            set { attachmentIndex = value; }
        }
        public byte JointIndex
        {
            get { return jointIndex; }
            set { jointIndex = value; }
        }
        public string JointName
        {
            get { return jointName; }
            set { jointName = value; }
        }
        public FrameObjectBase? Attachment
        {
            get { return attachment; }
            set { attachment = value; }
        }

        public AttachmentReference()
        {
            attachmentIndex = -1;
            jointIndex = 0;
            jointName = string.Empty;
            attachment = null;
        }

        public AttachmentReference(AttachmentReference other)
        {
            attachmentIndex = other.attachmentIndex;
            jointIndex = other.jointIndex;
        }
    }


    public class HitBoxInfo
    {
        uint unk;
        Short3 pos = null!;
        Short3 size = null!;

        public uint Unk
        {
            get { return unk; }
            set { unk = value; }
        }
        public Short3 Position
        {
            get { return pos; }
            set { pos = value; }
        }
        public Short3 Size
        {
            get { return size; }
            set { size = value; }
        }

        /// <summary>Empty hit box for the native-boundary mapper.</summary>
        internal HitBoxInfo()
        {
        }

        public HitBoxInfo(HitBoxInfo other)
        {
            unk = other.unk;
            pos = new Short3(other.pos);
            size = new Short3(other.size);
        }
    }
    public class WeightedByMeshSplit
    {
        ushort blendIndex;
        BlendMeshSplitInfo[] data = null!;
        string jointName;

        public ushort BlendIndex
        {
            get { return blendIndex; }
            set { blendIndex = value; }
        }
        public BlendMeshSplitInfo[] Data
        {
            get { return data; }
            set { data = value; }
        }
        public string JointName
        {
            get { return jointName; }
            set { jointName = value; }
        }

        /// <summary>Empty split for the native-boundary mapper.</summary>
        internal WeightedByMeshSplit()
        {
            jointName = "";
        }

        public WeightedByMeshSplit(WeightedByMeshSplit other)
        {
            blendIndex = other.blendIndex;
            data = new BlendMeshSplitInfo[other.data.Length];
            for (int i = 0; i != other.data.Length; i++)
                data[i] = other.data[i];
            jointName = other.jointName;
        }

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(jointName))
            {
                return jointName.ToString();
            }

            return "";
        }
    }

    public class BlendMeshSplitInfo
    {
        MiniMaterialBurst[] data = null!;

        public MiniMaterialBurst[] Data
        {
            get { return data; }
            set { data = value; }
        }

        /// <summary>Empty info for the native-boundary mapper.</summary>
        internal BlendMeshSplitInfo()
        {
        }

        public BlendMeshSplitInfo(BlendMeshSplitInfo other)
        {
            data = new MiniMaterialBurst[other.data.Length];
            for (int i = 0; i != other.data.Length; i++)
                data[i] = other.data[i];
        }
    }

    public class MiniMaterialBurst
    {
        ushort materialIndex;
        FacesBurst[] data = null!;

        public ushort MaterialIndex
        {
            get { return materialIndex; }
            set { materialIndex = value; }
        }
        public FacesBurst[] Data
        {
            get { return data; }
            set { data = value; }
        }

        /// <summary>Empty burst for the native-boundary mapper.</summary>
        public MiniMaterialBurst()
        {
        }

        public MiniMaterialBurst(MiniMaterialBurst other)
        {
            materialIndex = other.materialIndex;
            data = new FacesBurst[other.data.Length];
            for (int i = 0; i != other.data.Length; i++)
                data[i] = other.data[i];
        }
    }

    public class FacesBurst
    {
        ushort startIndex;
        ushort numFaces;

        public ushort StartIndex
        {
            get { return startIndex; }
            set { startIndex = value; }
        }
        public ushort NumFaces
        {
            get { return numFaces; }
            set { numFaces = value; }
        }

        /// <summary>Empty burst — for the native-boundary mapper and for rebuilding the table after a
        /// re-topologised mesh comes back from Blender.</summary>
        public FacesBurst()
        {
        }

        public FacesBurst(FacesBurst other)
        {
            startIndex = other.startIndex;
            numFaces = other.numFaces;
        }
    }
}
