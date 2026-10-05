using System.IO;
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
/// them: undo puts the old files back and redo the new ones.
/// </para>
/// <para>
/// Only a picture that was REPLACED is taken back. One the push introduced stays: the material that names it
/// was created by an edit of its own and outlives this one, and Blender — which has been told the push
/// arrived — sends the pixels again only when the image changes. Removed here, the texture was gone from
/// every archive while its material went on naming it, and the next push put that material on the object
/// with nothing to draw it from.
/// </para>
/// </summary>
internal sealed class TextureFilesEdit : IEditAction
{
    private readonly D3DImageHost _host;
    private readonly IReadOnlyList<AuthoredMaterialResolver.TextureChange> _changes;
    private readonly IReadOnlyList<FileInfo> _archives;
    private bool _onDisk = true; // the resolver has already written them when the push is applied

    /// <param name="archives">The archives whose folders the changes are in — each has to be built again
    /// after an undo or a redo, exactly as after the push itself.</param>
    public TextureFilesEdit(D3DImageHost host, IReadOnlyList<AuthoredMaterialResolver.TextureChange> changes,
        IEnumerable<FileInfo> archives)
    {
        _host = host;
        _changes = [.. changes.Where(c => c.Before.Exists)];
        _archives = [.. archives];
    }

    /// <summary>Whether there is anything here to take back — a push that only introduced textures has
    /// nothing, and an entry made of it would be a Ctrl+Z that does nothing.</summary>
    public bool IsEmpty => _changes.Count == 0;

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
    // now behind. A Build since the push took those archives off the build list; without putting them back
    // an undo left the folder with the old picture, the game's archive with the new one and nothing to say so.
    private void Reload()
    {
        if (_changes.Count == 0) return;
        _host.MaterialEditing.ReloadTextureFiles(_changes.Select(c => c.File));
        foreach (FileInfo archive in _archives) _host.MarkArchiveModified(archive);
    }
}
