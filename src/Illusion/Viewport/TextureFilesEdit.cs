using Illusion.Assets.Bridge;
using Illusion.Assets.Textures;
using Illusion.Domain;

namespace Illusion.Viewport;

/// <summary>
/// The texture files one Blender push wrote into archive folders, as part of that push's undo entry.
/// <para>
/// A material remembers which texture it NAMES, and the bridge repaints an authored texture in place — so
/// after a repaint the catalog has nothing to undo, and Ctrl+Z took back the mesh while the old picture was
/// gone for good (or took back some earlier, unrelated edit when the push had changed nothing else). The
/// bytes each name held before and after are kept here instead, with the manifest entries that go with
/// them: undo puts the old files back — or removes a texture the push introduced — and redo the new ones.
/// </para>
/// </summary>
internal sealed class TextureFilesEdit : IEditAction
{
    private readonly D3DImageHost _host;
    private readonly IReadOnlyList<AuthoredMaterialResolver.TextureChange> _changes;
    private bool _onDisk = true; // the resolver has already written them when the push is applied

    public TextureFilesEdit(D3DImageHost host, IReadOnlyList<AuthoredMaterialResolver.TextureChange> changes)
    {
        _host = host;
        _changes = [.. changes];
    }

    public void Undo()
    {
        if (!_onDisk) return;
        for (int i = _changes.Count - 1; i >= 0; i--)
        {
            ArchiveTextureWriter.Restore(_changes[i].Dir, _changes[i].File, _changes[i].Before);
        }
        _onDisk = false;
        Reload();
    }

    public void Redo()
    {
        if (_onDisk) return;
        foreach (AuthoredMaterialResolver.TextureChange change in _changes)
        {
            ArchiveTextureWriter.Restore(change.Dir, change.File, change.After);
        }
        _onDisk = true;
        Reload();
    }

    // The folders changed under a renderer that holds the old pictures, and under archives whose .sds is
    // now behind (or, after an undo, no longer ahead in that respect — but still not what was last packed).
    private void Reload() =>
        _host.MaterialEditing.ReloadTextureFiles(_changes.Select(c => c.File));
}
