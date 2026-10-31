using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Windows.Data;
using Illusion.Domain;
using Illusion.Rendering.Gpu;

namespace Illusion.Scene;

/// <summary>
/// Scene tree node — recursive: folder → SDS → frame hierarchy → mesh. A mesh has either <see cref="Mesh"/>
/// (GPU) or <see cref="Pending"/> (awaiting upload). The eye cascades visibility over the whole branch.
/// </summary>
public sealed class SceneNode : INotifyPropertyChanged
{
    private string _name;
    /// <summary>Display name (the tree row binds this). Settable so a property edit that renames a frame object
    /// updates the row live.</summary>
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Raise(nameof(Name)); } }
    }
    public string Kind { get; }               // Folder / Sds / FrameResource / Scene / Frame / Mesh / Light / …
    public string Category { get; set; } = "Normal"; // for scenes: Proxy / Normal (filters)
    public bool IsContainer { get; }          // folder/SDS/frame (bold font, has a branch)
    public bool IsProxy { get; }

    /// <summary>Parent node in the tree (null for roots). Set by <see cref="AddChild"/>.</summary>
    public SceneNode? Parent { get; private set; }

    /// <summary>
    /// Backing data object this node represents: an <see cref="IFrameNode"/> (transformable frame/mesh),
    /// an <see cref="IFrameScene"/> (scene folder), an <see cref="ISceneDocument"/> (the SDS's loaded frame
    /// resource — the save/build unit), or null (source folder / SDS container). Typed as the Domain port so
    /// no consumer ever depends on a concrete format backend.
    /// </summary>
    public ISceneSource? Source { get; set; }
    public ISceneSource? GeometrySource { get; set; }

    /// <summary>
    /// Which level of detail of <see cref="Source"/> this row holds. Zero for everything except the "LOD n"
    /// rows a multi-level mesh grows, and those share their frame's <see cref="Source"/> — so selecting one,
    /// dragging its gizmo or sending it to Blender all act on the same object, at THIS level.
    /// </summary>
    public int Lod { get; init; }

    private GpuMesh? _mesh;
    /// <summary>GPU mesh of the leaf (assigned during batched upload). Before that — <see cref="Pending"/>.</summary>
    public GpuMesh? Mesh
    {
        get => _mesh;
        set { _mesh = value; if (_mesh != null) _mesh.Visible = _visible; }
    }
    /// <summary>CPU mesh awaiting upload to the GPU (for mesh nodes).</summary>
    public MeshData? Pending { get; set; }

    public ObservableCollection<SceneNode> Children { get; } = new();

    // Lazy view creation: there are thousands of nodes in the tree, but only the expanded branch is actually bound
    // (TreeView is virtualized) — we don't create thousands of ICollectionView in BeginBuild.
    private ICollectionView? _childrenView;
    public ICollectionView ChildrenView
    {
        get
        {
            if (_childrenView == null)
            {
                _childrenView = CollectionViewSource.GetDefaultView(Children);
                _childrenView.Filter = o => o is SceneNode n && n.HasSearchMatch;
            }
            return _childrenView;
        }
    }

    public SceneNode(string name, string kind, bool isContainer)
    {
        _name = name;
        Kind = kind;
        IsContainer = isContainer;
        IsProxy = name != null && name.Contains("proxy", StringComparison.OrdinalIgnoreCase);
    }

    public void AddChild(SceneNode child)
    {
        child.Parent = this;
        child.PropertyChanged += OnChildChanged;
        Children.Add(child);
        RecomputeAggregate();
    }

    /// <summary>
    /// Adds many children at once, recomputing the visibility aggregate a single time at the end. <see cref="AddChild"/>
    /// recomputes on every call, which walks the children built so far — filling a crash row with a thousand
    /// placements one by one is quadratic in the row's size. Same wiring otherwise.
    /// </summary>
    public void AddChildren(IReadOnlyList<SceneNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        foreach (SceneNode child in children)
        {
            child.Parent = this;
            child.PropertyChanged += OnChildChanged;
            Children.Add(child);
        }
        RecomputeAggregate();
    }

    /// <summary>Re-parents <paramref name="child"/> under this node at <paramref name="index"/> (clamped). Used to
    /// restore a node to its original slot on undo of a delete; the child keeps its existing change subscription
    /// (it was added once via <see cref="AddChild"/>), so this does not re-subscribe.</summary>
    public void InsertChild(int index, SceneNode child)
    {
        child.Parent = this;
        Children.Insert(Math.Clamp(index, 0, Children.Count), child);
        RecomputeAggregate();
    }

    /// <summary>Moves this node under a new parent at the given index (removes it from the old parent, re-wires the
    /// visibility subscription, inserts under the new one). The bound tree updates automatically. Used by reparent.</summary>
    public void MoveTo(SceneNode newParent, int index)
    {
        if (ReferenceEquals(newParent, Parent) && Parent.Children.IndexOf(this) == index) return;
        if (Parent != null)
        {
            Parent.Children.Remove(this);
            PropertyChanged -= Parent.OnChildChanged;
        }
        SceneNode? oldParent = Parent;
        Parent = newParent;
        PropertyChanged += newParent.OnChildChanged;
        int clamped = Math.Max(0, Math.Min(index, newParent.Children.Count));
        newParent.Children.Insert(clamped, this);
        // Both ends of the move must refresh their cached aggregate (the old parent may have lost
        // its only visible child; the new one may have gained a hidden one).
        oldParent?.RecomputeAggregate();
        newParent.RecomputeAggregate();
    }

    /// <summary>Yields this node and every descendant that carries a GPU mesh (a renderable leaf).</summary>
    public IEnumerable<SceneNode> DescendantMeshLeaves()
    {
        if (Mesh != null) yield return this;
        foreach (SceneNode c in Children)
            foreach (SceneNode leaf in c.DescendantMeshLeaves())
                yield return leaf;
    }

    /// <summary>Union of the world-space AABBs of all mesh leaves under this node. False when there are none.</summary>
    public bool TryGetWorldBounds(out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        bool any = false;
        foreach (SceneNode leaf in DescendantMeshLeaves())
        {
            // Skip instanced (city_crash) meshes: their AABB spans every copy across the whole map, so it is
            // not a meaningful bound for a single selected object.
            if (leaf.Mesh == null || leaf.Mesh.Instanced) continue;
            min = Vector3.Min(min, leaf.Mesh.BoundsMin);
            max = Vector3.Max(max, leaf.Mesh.BoundsMax);
            any = true;
        }
        return any;
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsVisible)) RecomputeAggregate();
    }

    // Container visibility is CACHED in _visible ("any descendant visible"), not recomputed on
    // every read. A recursive getter plus a per-node PropertyChanged bubble made hiding a big
    // container O(n^2) — a measured ~154 ms freeze on arpatro when the FrameResource eye was
    // toggled. The getter is now O(1); the aggregate is maintained bottom-up.
    private bool _visible = true;
    // Set while THIS node's SetVisible cascade runs, so a descendant's mid-cascade Raise does not
    // trigger a redundant (and wrong, mid-flight) aggregate recompute here.
    private bool _cascading;

    /// <summary>The eye: for a leaf — mesh visibility; for a container — whether at least one descendant is visible; set cascades.</summary>
    public bool IsVisible
    {
        get => _mesh?.Visible ?? _visible;   // leaf: mesh (or pending _visible); container: cached aggregate
        set => SetVisible(value);
    }

    private void SetVisible(bool value)
    {
        // Leaf already at the target: nothing to do. (A container is never early-outed on its
        // aggregate — a partially-visible container must still fully re-show/re-hide its subtree.)
        if (Children.Count == 0 && (_mesh?.Visible ?? _visible) == value) return;

        _cascading = true;
        try
        {
            _visible = value;   // every descendant becomes `value`, so the container aggregate is `value`
            if (_mesh != null) _mesh.Visible = value;
            foreach (SceneNode c in Children) c.SetVisible(value);
        }
        finally { _cascading = false; }
        Raise(nameof(IsVisible)); // notify this node's own eye binding; ancestors update via the bubble below
    }

    // A descendant's visibility changed: refresh this container's cached aggregate from its DIRECT
    // children (each getter is O(1) now) and bubble up only when the aggregate actually flips.
    private void RecomputeAggregate()
    {
        if (_cascading) return; // our own cascade sets the aggregate explicitly
        bool any = false;
        foreach (SceneNode c in Children)
        {
            if (c.IsVisible) { any = true; break; }
        }
        if (any == _visible) return; // unchanged → stop the bubble (this is what kills the O(n^2))
        _visible = any;
        Raise(nameof(IsVisible));
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; Raise(nameof(IsExpanded)); } }
    }

    // Selection-membership flag, set ONLY by the viewport's selection host for every node in the multi-selection;
    // the tree row's highlight DataTrigger binds it directly. NOT bound to the TreeViewItem's own IsSelected —
    // that would fight the TreeView's single-selection when several rows are highlighted at once.
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; Raise(nameof(IsSelected)); } }
    }

    /// <summary>Expands every ancestor so this node's tree row is realized and visible (used after a viewport pick).</summary>
    public void ExpandAncestors()
    {
        for (SceneNode? p = Parent; p != null; p = p.Parent) p.IsExpanded = true;
    }

    /// <summary>The nearest ancestor (or self) whose <see cref="Source"/> is a loaded document
    /// (<see cref="ISceneDocument"/>) — the save unit an edited object lives under; null if there is none.</summary>
    public SceneNode? OwningDocumentNode()
    {
        for (SceneNode? n = this; n != null; n = n.Parent)
            if (n.Source is ISceneDocument) return n;
        return null;
    }

    public bool HasSearchMatch =>
        SceneSearch.Matches(Name) || Children.Any(c => c.HasSearchMatch);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
