namespace Illusion.Formats.Frames;

public enum FrameEntryRefTypes
{
    Geometry,
    Material,
    BlendInfo,
    Skeleton,
    SkeletonHierarchy,
    Parent1,
    Parent2
}

public class FrameEntry
{
    //All frame entries have their own ID, this is used so we can link the entries with each other instead of using Indexes, like Mafia II uses.
    protected int refID;
    protected Dictionary<FrameEntryRefTypes, int> refs = new Dictionary<FrameEntryRefTypes, int>();

    protected FrameResource OwningResource;

    /// <summary>The resource this entry belongs to — the whole file it will be written back into.</summary>
    public FrameResource Resource => OwningResource;

    public int RefID
    {
        set { refID = value; }
        get { return refID; }
    }
    public Dictionary<FrameEntryRefTypes, int> Refs
    {
        set { refs = value; }
        get { return refs; }
    }

    public FrameEntry(FrameResource OwningResource)
    {
        refID = RefManager.GetNewRefID();
        this.OwningResource = OwningResource;
    }

    public FrameEntry(FrameEntry entry)
    {
        refID = RefManager.GetNewRefID();
        refs = new Dictionary<FrameEntryRefTypes, int>();

        for (int i = 0; i < entry.refs.Count; i++)
        {
            refs.Add(entry.refs.ElementAt(i).Key, entry.refs.ElementAt(i).Value);
        }

        OwningResource = entry.OwningResource;
    }

    /// <summary>
    /// Hands the entry to another resource — the one step a copy constructor cannot take, since a copy is
    /// born in the resource of its original. Nothing else changes: the caller registers the entry there and
    /// rewires every reference it holds, which still name blocks and parents of the resource it came from.
    /// </summary>
    public void MoveTo(FrameResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        OwningResource = resource;
    }

    public void AddRef(FrameEntryRefTypes type, int objRef)
    {
        refs.Add(type, objRef);
    }

    public void ReplaceRef(FrameEntryRefTypes type, int objRef)
    {
        if (!refs.ContainsKey(type))
        {
            AddRef(type, objRef);
            return;
        }

        refs[type] = objRef;
    }

    public void SubRef(FrameEntryRefTypes refType)
    {
        refs.TryRemove(refType);
    }

    public override string ToString()
    {
        return "Frame Entry";
    }
}
