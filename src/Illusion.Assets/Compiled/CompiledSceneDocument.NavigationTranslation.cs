using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Translates one complete, non-streamed NAV_PATH resource. The returned edit is applied.</summary>
    public IEditAction? TranslateNavigationSection(int sectionIndex, Vector3 delta, out string? refusal)
    {
        refusal = null;
        if ((uint)sectionIndex >= _navMeshes.Count) { refusal = "Navigation section index is out of range."; return null; }
        try { return new NavigationTranslationEdit(_navMeshes[sectionIndex].TranslateIsolatedSection(delta)); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        { refusal = ex.Message; return null; }
    }

    private sealed class NavigationTranslationEdit(Mafia3NavigationTranslation translation) : IEditAction
    {
        public void Undo() => translation.Undo();
        public void Redo() => translation.Redo();
    }
}
