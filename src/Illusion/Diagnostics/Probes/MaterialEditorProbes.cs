using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Materials;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;
using Illusion.ViewModels;
using Illusion.Viewport;
using Illusion.Views;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the material editor: the preview sphere generator, the MTL catalog port
/// (browse / texture rebind / create / delete, all in-memory), the SetTextureFor file roundtrip on a TEMP
/// copy of default.mtl, mesh-slot reassignment, and the tile grid + editor window layout. The GPU probe
/// covers the two-concurrent-GPU-stacks path and the sphere thumbnail renderer.</summary>
internal static class MaterialEditorProbes
{
    // Headless logic probe (game data for the catalog parts; skips gracefully without it).
    // Output: %TEMP%\illusion_material_editor.txt (+ illusion_material_tiles.png / illusion_material_editor.png)
    internal static void RunEditorProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_material_editor.txt");
        var sb = new StringBuilder();
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length == 0 ? "" : " — " + detail)}");
            if (ok) passed++; else failed++;
        }

        try
        {
            CheckSphere(Check);

            if (!InitEnv(out string? err))
            {
                sb.AppendLine("catalog parts skipped — no game path (" + (err ?? "") + ")");
            }
            else
            {
                MaterialInfo? sample = CheckCatalog(Check, sb, out string? defaultLib);
                CheckMtlFileRoundtrip(Check, sb);
                CheckSlotReassign(Check, sb, district);
                if (sample != null && defaultLib != null) CheckUi(Check, sb, sample);
                CheckAssignFlow(Check, sb, district);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            failed++;
        }
        finally
        {
            sb.Insert(0, $"MATERIAL EDITOR PROBE: {(failed == 0 ? "PASS" : "FAIL")} ({passed} passed, {failed} failed)\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static void CheckSphere(Action<string, bool, string> check)
    {
        const int rings = 32, segments = 48;
        MeshData sphere = SphereMesh.Create(new MeshPart(0, 0, "d.dds", "n.dds", "s.dds", 123UL), rings, segments);
        int expectedVerts = (rings + 1) * (segments + 1);
        int expectedIndices = segments * (2 * rings - 2) * 3; // pole rows contribute one triangle per quad
        check("sphere vertex count", sphere.VertexCount == expectedVerts,
            $"{sphere.VertexCount} vs {expectedVerts}");
        check("sphere index count (degenerate cap quads skipped)", sphere.Indices.Length == expectedIndices,
            $"{sphere.Indices.Length} vs {expectedIndices}");

        bool unitNormals = true, posEqualsNormal = true, uvInRange = true, orthonormal = true, indicesInRange = true;
        for (int i = 0; i < sphere.VertexCount; i++)
        {
            Vector3 n = sphere.Normals[i];
            Vector3 t = sphere.Tangents![i];
            Vector3 b = sphere.Binormals![i];
            if (MathF.Abs(n.Length() - 1f) > 1e-4f || MathF.Abs(t.Length() - 1f) > 1e-4f
                || MathF.Abs(b.Length() - 1f) > 1e-4f) unitNormals = false;
            if ((sphere.Positions[i] - n).Length() > 1e-5f) posEqualsNormal = false;
            Vector2 uv = sphere.UVs![i];
            if (uv.X is < 0f or > 1f || uv.Y is < 0f or > 1f) uvInRange = false;
            if (MathF.Abs(Vector3.Dot(n, t)) > 1e-4f || MathF.Abs(Vector3.Dot(n, b)) > 1e-4f
                || MathF.Abs(Vector3.Dot(t, b)) > 1e-4f) orthonormal = false;
        }
        foreach (uint idx in sphere.Indices)
            if (idx >= sphere.VertexCount) indicesInRange = false;

        check("sphere normals/tangents/binormals are unit-length", unitNormals, "");
        check("sphere positions equal normals (unit radius)", posEqualsNormal, "");
        check("sphere UVs stay in [0,1]", uvInRange, "");
        check("sphere TBN frames are orthonormal", orthonormal, "");
        check("sphere indices stay in range", indicesInRange, "");
        check("sphere part carries material hash + textures + full range",
            sphere.Parts.Length == 1 && sphere.Parts[0].MaterialHash == 123UL
            && sphere.Parts[0].DiffuseTexture == "d.dds" && sphere.Parts[0].IndexCount == sphere.Indices.Length, "");
    }

    // Browses the real loaded catalog and exercises the in-memory mutations (nothing is saved: edits are
    // reverted, the created material is removed, and the probe process never calls SaveDirty).
    private static MaterialInfo? CheckCatalog(Action<string, bool, string> check, StringBuilder sb, out string? defaultLib)
    {
        IMaterialCatalog cat = MafiaMaterialCatalog.Instance;
        IReadOnlyList<string> libs = cat.Libraries;
        check("MTL libraries loaded", libs.Count > 0, string.Join(", ", libs));
        defaultLib = libs.FirstOrDefault(l => l.Equals("default.mtl", StringComparison.OrdinalIgnoreCase))
                     ?? libs.FirstOrDefault();
        if (defaultLib == null) return null;

        IReadOnlyList<MaterialSummary> mats = cat.GetMaterials(defaultLib);
        check("default.mtl enumerates a real material set", mats.Count > 1000, mats.Count + " materials");

        // A material with a bound S000 texture — the sample for the edit/UI parts.
        MaterialInfo? sample = null;
        foreach (MaterialSummary m in mats)
        {
            MaterialInfo? info = cat.GetMaterial(m.Hash);
            if (info != null && info.TextureSlots.Any(s => s.SlotId == "S000" && !string.IsNullOrEmpty(s.TextureName)))
            {
                sample = info;
                break;
            }
        }
        check("a material with a bound S000 exists", sample != null, sample?.Name ?? "");
        if (sample == null) return null;
        check("LibraryOf resolves the sample", cat.LibraryOf(sample.Hash) != null, cat.LibraryOf(sample.Hash) ?? "");

        // In-memory texture rebind + revert.
        string? original = cat.GetTexture(sample.Hash, "S000");
        check("GetTexture reads the bound name", !string.IsNullOrEmpty(original), original ?? "");
        bool set = cat.SetTexture(sample.Hash, "S000", "illusion_probe.dds");
        check("SetTexture applies in memory", set && cat.GetTexture(sample.Hash, "S000") == "illusion_probe.dds", "");
        check("renderer resolution follows the edit",
            MafiaMaterials.GetMaterialTextures(sample.Hash).Diffuse == "illusion_probe.dds", "");
        check("catalog turns dirty after an edit", cat.HasUnsavedChanges, "");
        cat.SetTexture(sample.Hash, "S000", original!);
        check("SetTexture reverts", cat.GetTexture(sample.Hash, "S000") == original, "");

        // Sampler slot add / remove / restore — in-memory.
        check("KnownSamplerSlots lists the S-codes", cat.KnownSamplerSlots.Count > 3,
            cat.KnownSamplerSlots.Count + " slots");
        string freeSlot = cat.KnownSamplerSlots.First(d => cat.GetTexture(sample.Hash, d.Id) == null).Id;
        check("AddSampler adds an empty slot",
            cat.AddSampler(sample.Hash, freeSlot) && cat.GetTexture(sample.Hash, freeSlot) == "", freeSlot);
        check("duplicate AddSampler is refused", !cat.AddSampler(sample.Hash, freeSlot), "");
        object? slotToken = cat.RemoveSampler(sample.Hash, freeSlot);
        check("RemoveSampler hands back a restore token",
            slotToken != null && cat.GetTexture(sample.Hash, freeSlot) == null, "");
        check("RestoreSampler puts the slot back",
            slotToken != null && cat.RestoreSampler(sample.Hash, slotToken)
            && cat.GetTexture(sample.Hash, freeSlot) == "", "");
        cat.RemoveSampler(sample.Hash, freeSlot); // leave the in-memory material clean

        // Shader parameter edit — in-memory, same length enforced.
        MaterialParamInfo? paramInfo = sample.Parameters.FirstOrDefault(p => p.Values.Count > 0);
        if (paramInfo != null)
        {
            IReadOnlyList<float> before = cat.GetParameter(sample.Hash, paramInfo.ParamId)!;
            List<float> edited = before.Select(v => v + 0.5f).ToList();
            check("SetParameter applies in memory",
                cat.SetParameter(sample.Hash, paramInfo.ParamId, edited)
                && cat.GetParameter(sample.Hash, paramInfo.ParamId)!.SequenceEqual(edited), paramInfo.ParamId);
            check("a wrong float count is refused",
                !cat.SetParameter(sample.Hash, paramInfo.ParamId, edited.Append(1f).ToList()), "");
            cat.SetParameter(sample.Hash, paramInfo.ParamId, before.ToList());
            check("parameter reverted", cat.GetParameter(sample.Hash, paramInfo.ParamId)!.SequenceEqual(before), "");
        }

        // Global texture index — the editor's whole-mirror resolution scope.
        Assets.Textures.TextureSearchIndex.EnsureBuilt();
        check("texture index scans the resources mirror",
            Assets.Textures.TextureSearchIndex.IsBuilt && Assets.Textures.TextureSearchIndex.Count > 10000,
            Assets.Textures.TextureSearchIndex.Count + " textures");
        bool anyResolves = false;
        int probed = 0;
        foreach (MaterialSummary m in mats)
        {
            if (probed++ > 200) break;
            string? diffuse = cat.GetMaterial(m.Hash)?.TextureSlots
                .FirstOrDefault(s => s.SlotId == "S000")?.TextureName;
            if (diffuse != null && Assets.Textures.TextureSearchIndex.FindPath(diffuse) != null)
            {
                anyResolves = true;
                break;
            }
        }
        check("the index resolves real material textures", anyResolves, "");

        // Create / duplicate refusal / remove / restore — all in-memory.
        const string name = "ILLUSION_PROBE_MATERIAL";
        ulong? created = cat.CreateMaterial(defaultLib, name);
        check("CreateMaterial mints a default-preset material",
            created != null && cat.GetMaterial(created.Value)?.Name == name, "");
        check("duplicate name is refused", cat.CreateMaterial(defaultLib, name) == null, "");
        object? token = created != null ? cat.RemoveMaterial(created.Value) : null;
        check("RemoveMaterial hands back a restore token",
            token != null && created != null && cat.GetMaterial(created.Value) == null, "");
        check("RestoreMaterial puts the exact material back",
            token != null && cat.RestoreMaterial(token) && cat.GetMaterial(created!.Value) != null, "");

        // Rename — the hash follows the name (FNV64) and the entry keeps its dictionary position
        // (save order is part of byte-fidelity). All in-memory, renamed back before cleanup.
        if (created != null)
        {
            const string renamed = "ILLUSION_PROBE_MATERIAL_RENAMED";
            int posBefore = cat.GetMaterials(defaultLib).Select(m => m.Hash).ToList().IndexOf(created.Value);
            ulong? renamedHash = cat.RenameMaterial(created.Value, renamed);
            check("RenameMaterial re-keys to the FNV64 of the new name",
                renamedHash == Fnv64.Hash(renamed)
                && cat.GetMaterial(renamedHash!.Value)?.Name == renamed
                && cat.GetMaterial(created.Value) == null, "");
            check("rename keeps the dictionary position",
                renamedHash != null
                && cat.GetMaterials(defaultLib).Select(m => m.Hash).ToList().IndexOf(renamedHash.Value) == posBefore,
                $"position {posBefore}");
            string? taken = mats.FirstOrDefault(m => !string.IsNullOrEmpty(m.Name))?.Name;
            check("rename to a taken name is refused",
                renamedHash != null && taken != null && cat.RenameMaterial(renamedHash.Value, taken) == null,
                taken ?? "");
            check("rename back restores the original hash",
                renamedHash != null && cat.RenameMaterial(renamedHash.Value, name) == created.Value, "");

            // Undo fidelity on a vanilla material: its stored hash is not always FNV64(name), so the
            // restore path must pin the exact original hash, not re-derive one.
            ulong vanillaHash = sample.Hash;
            string vanillaName = sample.Name ?? "";
            ulong? away = cat.RenameMaterial(vanillaHash, "ILLUSION_PROBE_MATERIAL_AWAY");
            check("undo-style restore pins a vanilla material's stored hash",
                away != null
                && cat.RenameMaterial(away.Value, vanillaName, vanillaHash) == vanillaHash
                && cat.GetMaterial(vanillaHash)?.Name == vanillaName,
                $"0x{vanillaHash:X}" + (vanillaHash == Fnv64.Hash(vanillaName) ? " (=FNV64)" : " (!=FNV64 of name)"));
        }

        // Parameter add/remove — in-memory, the canonical float count comes from the loaded libraries.
        check("KnownParameters carries canonical lengths from the game data",
            cat.KnownParameters.Count(d => d.Length != null) > 20,
            cat.KnownParameters.Count(d => d.Length != null) + " with lengths");
        if (created != null)
        {
            ParamDescriptor? missing = cat.KnownParameters.FirstOrDefault(d =>
                d.Length is > 0 && cat.GetParameter(created.Value, d.Id) == null);
            if (missing != null)
            {
                var vals = Enumerable.Repeat(0.5f, missing.Length!.Value).ToList();
                check("AddParameter creates a missing parameter",
                    cat.AddParameter(created.Value, missing.Id, vals)
                    && cat.GetParameter(created.Value, missing.Id)!.SequenceEqual(vals), missing.Id);
                check("duplicate AddParameter is refused", !cat.AddParameter(created.Value, missing.Id, vals), "");
                check("RemoveParameter undoes the add",
                    cat.RemoveParameter(created.Value, missing.Id)
                    && cat.GetParameter(created.Value, missing.Id) == null, "");
                check("AddParameter enforces the canonical float count",
                    !cat.AddParameter(created.Value, missing.Id, vals.Append(1f).ToList()), "");
            }
        }
        if (created != null) cat.RemoveMaterial(created.Value); // leave the in-memory collection clean
        sb.AppendLine($"catalog sample: {sample.Name} (0x{sample.Hash:X}) S000={original}");
        return sample;
    }

    // SetTextureFor → WriteMatFile → ReadMatFile on a TEMP copy of the real default.mtl — the write path the
    // editor's Save will take, exercised end to end without touching the game folder.
    private static void CheckMtlFileRoundtrip(Action<string, bool, string> check, StringBuilder sb)
    {
        string? root = MafiaEnvironment.GameRoot;
        string src = Path.Combine(root ?? "", "edit", "materials", "default.mtl");
        if (root == null || !File.Exists(src))
        {
            sb.AppendLine("MTL file roundtrip skipped — default.mtl not found");
            return;
        }

        string tmpDir = Path.Combine(Path.GetTempPath(), "illusion_matedit_probe");
        Directory.CreateDirectory(tmpDir);
        try
        {
            string copy = Path.Combine(tmpDir, "default.mtl");
            File.Copy(src, copy, overwrite: true);
            var lib = new MaterialLibrary(MaterialVersion.V_57);
            lib.ReadMatFile(copy);
            IMaterial? target = lib.Materials.Values.FirstOrDefault(m => m.GetSamplerByKey("S000") != null);
            if (target == null)
            {
                check("a material with an S000 sampler exists in default.mtl", false, "");
                return;
            }
            target.SetTextureFor("S000", "illusion_probe.dds");

            // Also add a sampler slot and shift a parameter — the new editor mutations must survive the file.
            string[] candidates = { "S011", "S012", "S015", "S016", "S004" };
            string? addedSlot = candidates.FirstOrDefault(c => target.GetSamplerByKey(c) == null);
            if (addedSlot != null && target is Material_v57 tv57)
                tv57.Samplers = new List<MaterialSampler_v57>(tv57.Samplers) { new() { ID = addedSlot } };
            else if (addedSlot != null && target is Material_v58 tv58)
                tv58.Samplers = new List<MaterialSampler_v58>(tv58.Samplers) { new() { ID = addedSlot } };
            MaterialParameter? editedParam = target.Parameters.FirstOrDefault(p => p.Paramaters.Length > 0);
            float[]? paramAfter = null;
            if (editedParam != null)
            {
                paramAfter = editedParam.Paramaters.Select(v => v + 1f).ToArray();
                editedParam.Paramaters = paramAfter;
            }

            string edited = Path.Combine(tmpDir, "edited.mtl");
            lib.WriteMatFile(edited);

            var reloaded = new MaterialLibrary(MaterialVersion.V_57);
            reloaded.ReadMatFile(edited);
            IMaterial? back = reloaded.LookupMaterialByHash(target.GetMaterialHash());
            check("edited library reloads with the same material count",
                reloaded.Materials.Count == lib.Materials.Count,
                $"{reloaded.Materials.Count} vs {lib.Materials.Count}");
            check("texture edit survives the file roundtrip",
                back?.GetTextureByID("S000")?.String == "illusion_probe.dds", "");
            if (addedSlot != null)
                check("added sampler slot survives the file roundtrip",
                    back?.GetSamplerByKey(addedSlot) != null, addedSlot);
            if (editedParam != null && paramAfter != null)
                check("parameter edit survives the file roundtrip",
                    back?.GetParameterByKey(editedParam.ID)?.Paramaters.SequenceEqual(paramAfter) == true,
                    editedParam.ID);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // Mesh-slot reassignment through the IMaterialSlotEditor port on a real district — in-memory, restored.
    private static void CheckSlotReassign(Action<string, bool, string> check, StringBuilder sb, string district)
    {
        var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
        if (!sds.Exists)
        {
            sb.AppendLine($"slot reassignment skipped — {sds.FullName} missing");
            return;
        }
        string extracted = SdsMeshLoader.EnsureExtracted(sds);
        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        if (fr?.FrameObjects is not { Count: > 0 })
        {
            sb.AppendLine("slot reassignment skipped — no frame objects");
            return;
        }

        var doc = new SceneDocumentAdapter(fr, sds);
        IMaterialSlotEditor? editor = null;
        IMaterialListSource? list = null;
        foreach (FrameObjectSingleMesh mesh in fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
        {
            if (doc.Node(mesh) is IMaterialListSource src && src.GetMaterials().Count > 0)
            {
                list = src;
                editor = (IMaterialSlotEditor)src;
                break;
            }
        }
        if (editor == null || list == null)
        {
            sb.AppendLine("slot reassignment skipped — no mesh with materials");
            return;
        }

        ulong original = editor.GetSlotMaterial(0)!.Value;
        const ulong other = 0xABCDEF12345678UL; // any distinct hash — resolution is not part of this check
        check("SetSlotMaterial repoints slot 0",
            editor.SetSlotMaterial(0, other) && editor.GetSlotMaterial(0) == other, "");
        check("GetMaterials reflects the reassignment", list.GetMaterials()[0].Hash == other, "");
        check("out-of-range slot is refused", !editor.SetSlotMaterial(9999, other), "");
        editor.SetSlotMaterial(0, original);
        check("slot restored", editor.GetSlotMaterial(0) == original, "");
    }

    // The FULL assign path exactly as the user drives it: editor opened from a mesh tile (ShowMaterial
    // with a context node+slot), another material picked in the list, the Assign button clicked — then
    // the mesh slot must hold the picked material, persist through save, be undoable, and REFUSE a
    // context node that a scene reload has replaced (the "assign silently does nothing" regression).
    private static void CheckAssignFlow(Action<string, bool, string> check, StringBuilder sb, string district)
    {
        var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
        if (!sds.Exists)
        {
            sb.AppendLine($"assign flow skipped — {sds.FullName} missing");
            return;
        }
        string extracted = SdsMeshLoader.EnsureExtracted(sds);
        FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
        if (fr?.FrameObjects is not { Count: > 0 })
        {
            sb.AppendLine("assign flow skipped — no frame objects");
            return;
        }

        // A mesh whose slot-0 material resolves in the loaded catalog (the mainstream tile-click case).
        var doc = new SceneDocumentAdapter(fr, sds);
        IMaterialSlotEditor? editor = null;
        foreach (FrameObjectSingleMesh mesh in fr.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
        {
            if (doc.Node(mesh) is IMaterialSlotEditor e
                && e.GetSlotMaterial(0) is { } h
                && MafiaMaterialCatalog.Instance.GetMaterial(h) != null)
            {
                editor = e;
                break;
            }
        }
        if (editor == null)
        {
            sb.AppendLine("assign flow skipped — no mesh with a catalog-resolvable slot 0");
            return;
        }
        ulong original = editor.GetSlotMaterial(0)!.Value;

        // Mirror the real tree shape (folder → doc node → mesh leaf, attached to the viewport tree):
        // undo's ApplySlot gates on IsInScene, and MarkFrameModified walks OwningDocumentNode.
        var viewport = new D3DImageHost();
        var docNode = new Scene.SceneNode("FrameResource", "FrameResource", true) { Source = doc };
        var node = new Scene.SceneNode("assign-probe-mesh", "Mesh", true) { Source = (ISceneSource)editor };
        docNode.AddChild(node);
        viewport.Tree.GetOrCreateFolder("assign-probe").AddChild(docNode);

        var window = new MaterialEditorWindow(viewport);
        window.ShowMaterial(original, node, 0);
        check("[asgn] assign button visible and enabled with a mesh context",
            window.AssignPanel.Visibility == Visibility.Visible && window.AssignBtn.IsEnabled, "");

        // Pick a DIFFERENT material in the list, the way a user does.
        MaterialSummary? other = window.MaterialList.Items.OfType<MaterialSummary>()
            .FirstOrDefault(m => m.Hash != original);
        if (other == null)
        {
            sb.AppendLine("assign flow skipped — the shown library has no second material");
            return;
        }
        window.MaterialList.SelectedItem = other;
        check("[asgn] list pick keeps the button enabled", window.AssignBtn.IsEnabled, "");

        window.AssignBtn.RaiseEvent(
            new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check("[asgn] Assign click repoints the mesh slot at the picked material",
            editor.GetSlotMaterial(0) == other.Hash,
            $"slot 0x{editor.GetSlotMaterial(0):X} want 0x{other.Hash:X}");
        check("[asgn] the assign marks the document dirty (Save/Build will persist it)",
            viewport.PendingBuildArchives().Any(a =>
                string.Equals(a.FullName, sds.FullName, StringComparison.OrdinalIgnoreCase)), "");

        // The assign must survive SaveFrameResource → fresh reload (UpdateFrameData / SanitizeFrameData run
        // inside WriteToStream and could rebuild the material block). RefIDs are NOT stable across a save,
        // so the mesh is tracked by counting slot-0 bindings of the assigned hash before vs after.
        static int CountSlot0(FrameResource? res, ulong hash) =>
            res?.FrameObjects?.Values.OfType<FrameObjectSingleMesh>()
                .Count(m => m.Refs.ContainsKey(Formats.Frames.FrameEntryRefTypes.Material)
                    && m.Material?.Materials is { Count: > 0 } l && l[0] is { Length: > 0 } ss
                    && ss[0].MaterialHash == hash) ?? -1;
        int wantBefore = CountSlot0(fr, other.Hash);
        string frFile = Formats.Archive.SdsManifest.Load(extracted).GetFiles("FrameResource")[0];
        byte[] snapshot = File.ReadAllBytes(frFile);
        try
        {
            SdsWriter.SaveFrameResource(fr, sds);
            FrameResource? fresh = SdsMeshLoader.OpenScene(extracted).FrameResource;
            int wantAfter = CountSlot0(fresh, other.Hash);
            check("[asgn] the assign survives save + fresh reload",
                wantBefore >= 1 && wantAfter == wantBefore, $"before={wantBefore} after={wantAfter}");
        }
        finally
        {
            File.WriteAllBytes(frFile, snapshot);
        }

        viewport.Undo();
        check("[asgn] the assign is undoable (slot back to the original)",
            editor.GetSlotMaterial(0) == original, $"slot 0x{editor.GetSlotMaterial(0):X}");

        // A scene reload (area/season switch, restore-from-backup) replaces the tree while the non-modal
        // editor stays open: the pinned context node goes stale — its GpuMesh is dead, so nothing visible
        // could change. The assign must REFUSE the dead node (the undo path and ImportBatch already gate
        // on IsInScene), and the window must drop its assign context when the scene changes.
        viewport.Tree.Clear();
        bool staleAccepted = viewport.AssignSlotMaterial(node, 0, other.Hash);
        check("[asgn] assigning to a node that left the scene is refused",
            !staleAccepted && editor.GetSlotMaterial(0) == original,
            $"accepted={staleAccepted} slot=0x{editor.GetSlotMaterial(0):X}");
        viewport.RaiseSceneChanged();
        check("[asgn] a scene change hides the assign button (stale context dropped)",
            window.AssignPanel.Visibility == Visibility.Collapsed, "");
        window.Close();
    }

    // The 2×N tile grid and the editor window bind and lay out (no live GPU — thumbnails stay null and the
    // preview viewport never loads; the layout and the data plumbing are what is probed).
    private static void CheckUi(Action<string, bool, string> check, StringBuilder sb, MaterialInfo sample)
    {
        var vms = new List<MaterialViewModel>
        {
            new(sample, 0),
            new(sample, 1),
            new(sample, 2),
        };
        const int width = 340;
        var host = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26)),
            Padding = new Thickness(10),
            Width = width,
            Child = new MaterialsView { Materials = vms },
        };
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(0, 0, width, host.DesiredSize.Height));
        host.UpdateLayout();
        check("tile grid lays out to a finite height",
            host.DesiredSize.Height > 0 && !double.IsInfinity(host.DesiredSize.Height),
            $"{host.DesiredSize.Height:F0}px");
        SavePng(host, width, (int)Math.Ceiling(host.DesiredSize.Height),
            Path.Combine(Path.GetTempPath(), "illusion_material_tiles.png"), sb);

        var viewport = new D3DImageHost();
        var window = new MaterialEditorWindow(viewport);
        // Opening bigger than the desktop puts the bottom edge under the taskbar on the smallest screen we
        // support; the size asked for in XAML is clamped in the constructor (WindowFit).
        check("editor window opens no larger than the desktop",
            window.Width <= SystemParameters.WorkArea.Width && window.Height <= SystemParameters.WorkArea.Height,
            $"{window.Width:F0}x{window.Height:F0} in a {SystemParameters.WorkArea.Width:F0}x{SystemParameters.WorkArea.Height:F0} work area");

        window.ShowMaterial(sample.Hash, null, 0);
        var nameRow = window.NamePanel.DataContext as NameEditRow;
        check("editor window binds the requested material",
            nameRow != null && nameRow.Text == (sample.Name ?? "")
            && nameRow.HashHex == "0x" + sample.Hash.ToString("X") && window.NamePanel.IsEnabled,
            nameRow?.Text ?? "(no row)");
        check("editor window lists every texture slot",
            window.SlotsList.Items.Count == sample.TextureSlots.Count,
            $"{window.SlotsList.Items.Count} vs {sample.TextureSlots.Count}");
        var paramRows = window.ParamsList.Items.Cast<ParamEditRow>().ToList();
        int knownMissing = MafiaMaterialCatalog.Instance.KnownParameters
            .Count(d => sample.Parameters.All(p => p.ParamId != d.Id));
        check("editor window lists the material's parameters and offers every known code",
            paramRows.Count(r => !r.IsNew) == sample.Parameters.Count
            && paramRows.Count == sample.Parameters.Count + knownMissing,
            $"{paramRows.Count} rows ({sample.Parameters.Count} carried + {knownMissing} offered)");
        var taken = new HashSet<string>(sample.TextureSlots.Select(s => s.SlotId), StringComparer.Ordinal);
        var offered = (window.AddSlotList.ItemsSource as IEnumerable<SlotDescriptor>)?.ToList()
                      ?? new List<SlotDescriptor>();
        check("add-slot choices exclude slots the material already has",
            offered.Count > 0 && offered.All(d => !taken.Contains(d.Id)),
            offered.Count + " offered");
        check("assign button stays hidden without a mesh context",
            window.AssignPanel.Visibility == Visibility.Collapsed, "");

        var content = (FrameworkElement)window.Content;
        // The probe renders the content visual alone — without the window's theme background the white
        // labels vanish on the transparent PNG, so stamp a Fluent-dark stand-in onto the root grid.
        if (content is System.Windows.Controls.Grid grid)
            grid.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        content.Measure(new Size(1000, 640));
        content.Arrange(new Rect(0, 0, 1000, 640));
        content.UpdateLayout();
        SavePng(content, 1000, 640, Path.Combine(Path.GetTempPath(), "illusion_material_editor.png"), sb);

        // The restyled Import window shares the same dictionary — a bad StaticResource key would only
        // surface at runtime, so construct it headless and render its content too. The live app runs under
        // Fluent dark (App.OnStartup pins ThemeMode.Dark, AFTER the probe path has returned), whose implicit
        // styles reach into control templates — render under it too, or theme regressions stay invisible here.
#pragma warning disable WPF0001 // ThemeMode is experimental; the app pins the same theme
        var import = new ImportWindow(viewport) { ThemeMode = ThemeMode.Dark };
#pragma warning restore WPF0001
        var fakeTargets = new[] { new { Display = "arpradelna.sds" } };
        import.TargetBox.ItemsSource = fakeTargets;
        import.TargetBox.SelectedItem = fakeTargets[0];
        // Fake preview rows so the render exercises the row template + type icons (anonymous rows
        // carry the same property names the bindings use).
        import.PreviewList.ItemsSource = new[]
        {
            new { Name = "suz", Type = "Mesh", Materials = "01", Note = "" },
            new { Name = "COL_suz", Type = "Collision", Materials = "Wood", Note = "target has no .col layer" },
        };
        var importContent = (FrameworkElement)import.Content;
        if (importContent is System.Windows.Controls.Grid importGrid)
            importGrid.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        importContent.Measure(new Size(560, double.PositiveInfinity));
        int importHeight = Math.Max(300, (int)Math.Ceiling(importContent.DesiredSize.Height));
        importContent.Arrange(new Rect(0, 0, 560, importHeight));
        importContent.UpdateLayout();
        check("import window constructs against the shared styles", true, "");
        SavePng(importContent, 560, importHeight, Path.Combine(Path.GetTempPath(), "illusion_import_window.png"), sb);
    }

    private static void SavePng(Visual visual, int w, int h, string path, StringBuilder sb)
    {
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (FileStream fs = File.Create(path)) enc.Save(fs);
        sb.AppendLine($"rendered {w}x{h}px -> {path}");
    }

    // GPU probe: two concurrent GPU stacks (the map viewport + the preview window scenario) and the sphere
    // thumbnail renderer against a real district's extracted textures.
    // Output: %TEMP%\illusion_material_gpu.txt + illusion_material_thumb.png
    internal static void RunMaterialGpuProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_material_gpu.txt");
        var sb = new StringBuilder();
        int passed = 0, failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            sb.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length == 0 ? "" : " — " + detail)}");
            if (ok) passed++; else failed++;
        }

        GpuContext? gpu1 = null, gpu2 = null;
        SceneRenderer? r1 = null, r2 = null;
        SharedRenderTarget? t1 = null, t2 = null;
        var thumbs = new MaterialThumbnailRenderer();
        try
        {
            // Two live GPU stacks at once — the editor window's preview beside the map viewport.
            gpu1 = new GpuContext();
            r1 = new SceneRenderer(gpu1) { Mode = RenderMode.Render, ShowSky = false };
            t1 = new SharedRenderTarget(gpu1, 160, 160);
            gpu2 = new GpuContext();
            r2 = new SceneRenderer(gpu2) { Mode = RenderMode.Render, ShowSky = false };
            t2 = new SharedRenderTarget(gpu2, 160, 160);
            foreach ((SceneRenderer r, SharedRenderTarget t) in new[] { (r1, t1), (r2, t2) })
            {
                r.AddMesh(SphereMesh.Create(new MeshPart(0, 0, null)));
                r.Camera.LookAt(new Vector3(0f, -2.5f, 0.9f), Vector3.Zero);
                r.Render(t);
            }
            int lit1 = CountLitPixels(RenderTargetReadback.Read(gpu1, t1));
            int lit2 = CountLitPixels(RenderTargetReadback.Read(gpu2, t2));
            Check("two concurrent GPU stacks both draw the sphere", lit1 > 500 && lit2 > 500,
                $"{lit1} / {lit2} lit px");

            // A material's COLOUR is as editable as its textures, and an edit has to reach the meshes already
            // on screen — the same live path a texture swap takes. This is the check that was missing when the
            // colour reached a mesh at load time and never again: a repaint left the viewport on the old one.
            const ulong probeHash = 0xC0FFEE01UL;
            GpuMesh tinted = r1.CreateMeshGpu(SphereMesh.Create(
                new MeshPart(0, 0, null, null, null, probeHash, new Vector4(0.25f, 0.5f, 0.75f, 1f))));
            Check("a mesh part carries the colour it was built with",
                tinted.Parts[0].Tint == new Vector4(0.25f, 0.5f, 0.75f, 1f), tinted.Parts[0].Tint.ToString());

            var repainted = new Vector4(0.9f, 0.1f, 0.1f, 1f);
            int rebound = tinted.RebindPartTextures(probeHash, null, null, null, repainted);
            Check("re-resolving a material carries its colour with it",
                rebound == 1 && tinted.Parts[0].Tint == repainted,
                $"{rebound} part(s) rebound, tint {tinted.Parts[0].Tint}");

            Check("reassigning a slot's material carries its colour too",
                tinted.SetPartMaterial(0, probeHash, null, null, null, Vector4.One)
                && tinted.Parts[0].Tint == Vector4.One, tinted.Parts[0].Tint.ToString());
            tinted.Dispose();

            // The colour field: a paint parameter is edited as a colour, and what it stores stays linear.
            // 0.27 linear is a muted dark red once gamma-corrected — a swatch showing 0.27 of full red
            // would be almost black and would not match what the viewport draws.
            var field = new ColorField { Value = "0.27, 0.02, 0.02, 1" };
            var swatch = field.Swatch as SolidColorBrush;
            Check("a colour parameter shows the colour the viewport shows",
                swatch != null && swatch.Color.R is > 130 and < 155 && swatch.Color.G is > 30 and < 55,
                swatch?.Color.ToString() ?? "no swatch");

            field.Value = "not, a, colour";
            Check("a parameter that is not a colour shows no swatch rather than a wrong one",
                field.Swatch is not SolidColorBrush { Color.A: > 0 }, field.Swatch.ToString() ?? "");

            // Picking through the hex box writes linear floats back and leaves the fourth channel alone.
            field.Value = "0.27, 0.02, 0.02, 1";
            field.HexBox.Text = "#FFFFFF";
            field.HexBox.RaiseEvent(new RoutedEventArgs(System.Windows.UIElement.LostFocusEvent));
            Check("picking a colour writes linear values back, keeping the channels it has no opinion about",
                field.Value.StartsWith("1, 1, 1", StringComparison.Ordinal) && field.Value.EndsWith(", 1", StringComparison.Ordinal),
                field.Value);

            // Thumbnail renderer against real textures.
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("thumbnail part skipped — no game path (" + (err ?? "") + ")");
            }
            else
            {
                var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
                if (!sds.Exists)
                {
                    sb.AppendLine($"thumbnail part skipped — {sds.FullName} missing");
                }
                else
                {
                    string extracted = SdsMeshLoader.EnsureExtracted(sds);
                    MafiaMaterialCatalog cat = MafiaMaterialCatalog.Instance;
                    MaterialInfo? textured = null;
                    foreach (string lib in cat.Libraries)
                    {
                        foreach (MaterialSummary m in cat.GetMaterials(lib))
                        {
                            MaterialInfo? info = cat.GetMaterial(m.Hash);
                            string? diffuse = info?.TextureSlots
                                .FirstOrDefault(s => s.SlotId == "S000")?.TextureName;
                            if (diffuse != null && File.Exists(Path.Combine(extracted, diffuse)))
                            {
                                textured = info;
                                break;
                            }
                        }
                        if (textured != null) break;
                    }
                    if (textured == null)
                    {
                        sb.AppendLine("thumbnail part skipped — no material resolves to an extracted .dds");
                    }
                    else
                    {
                        // NO district folders on purpose: resolution must come from the whole-mirror index.
                        Assets.Textures.TextureSearchIndex.EnsureBuilt();
                        string[] folders = Array.Empty<string>();
                        ImageSource? img = thumbs.Render(textured, folders);
                        Check("thumbnail renders via the global texture index (no district folders)",
                            img is BitmapSource, textured.Name ?? "");
                        if (img is BitmapSource bmp)
                        {
                            var pixels = new byte[MaterialThumbnailRenderer.Width * MaterialThumbnailRenderer.Height * 4];
                            bmp.CopyPixels(pixels, MaterialThumbnailRenderer.Width * 4, 0);
                            var colors = new HashSet<int>();
                            for (int i = 0; i < pixels.Length; i += 4)
                                colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
                            Check("thumbnail is not a flat frame", colors.Count > 16, colors.Count + " colors");
                            Check("thumbnail is cached by identity",
                                ReferenceEquals(img, thumbs.Render(textured, folders)), "");

                            var enc = new PngBitmapEncoder();
                            enc.Frames.Add(BitmapFrame.Create(bmp));
                            string png = Path.Combine(Path.GetTempPath(), "illusion_material_thumb.png");
                            using (FileStream fs = File.Create(png)) enc.Save(fs);
                            sb.AppendLine($"thumbnail ({textured.Name}) -> {png}");
                        }
                    }
                }
            }

            // The resource editor's texture page: the same trick at the texture's own size, off an extracted
            // .dds by path rather than by material slot. The game ships DDS, which WPF cannot decode and
            // nothing here decodes on the CPU — so if this stops working, opening a texture shows nothing.
            CheckTexturePreview(Check, sb);
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            failed++;
        }
        finally
        {
            thumbs.Dispose();
            r2?.Dispose(); t2?.Dispose(); gpu2?.Dispose();
            r1?.Dispose(); t1?.Dispose(); gpu1?.Dispose();
            sb.Insert(0, $"MATERIAL GPU PROBE: {(failed == 0 ? "PASS" : "FAIL")} ({passed} passed, {failed} failed)\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // One extracted .dds through the texture page's renderer: the header gives its size, the picture comes
    // back at that size (capped), and it is an image rather than a flat fill.
    private static void CheckTexturePreview(Action<string, bool, string> check, StringBuilder sb)
    {
        var car = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars", "shubert_38.sds"));
        if (!car.Exists) { sb.AppendLine("\ntexture preview skipped — no shubert_38"); return; }

        string extracted = SdsMeshLoader.EnsureExtracted(car);
        FileInfo? dds = new DirectoryInfo(extracted).GetFiles("*.dds")
            .Where(f => !f.Name.StartsWith("MIP_", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length).FirstOrDefault();
        if (dds == null) { sb.AppendLine("\ntexture preview skipped — no .dds in the car"); return; }

        (int Width, int Height)? size = TexturePreviewRenderer.ReadSize(dds);
        check("a texture's own size reads straight off its header", size is { Width: > 0, Height: > 0 },
            size is var (sw, sh) && size != null ? $"{dds.Name} {sw}x{sh}" : "unreadable");

        using var preview = new TexturePreviewRenderer();
        ImageSource? picture = preview.Render(dds);
        var bmp = picture as BitmapSource;
        check("a texture comes back as a picture at its own shape",
            bmp != null && size != null
            && Math.Abs((double)bmp.PixelWidth / bmp.PixelHeight - (double)size.Value.Width / size.Value.Height)
                < 0.02,
            bmp == null ? "nothing came back" : $"{bmp.PixelWidth}x{bmp.PixelHeight}");

        if (bmp != null)
        {
            var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
            bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
            var shades = new HashSet<int>();
            for (int i = 0; i + 3 < pixels.Length; i += 4)
                shades.Add((pixels[i] << 16) | (pixels[i + 1] << 8) | pixels[i + 2]);
            check("the picture is the texture, not a flat fill", shades.Count > 16, shades.Count + " colours");
            check("a texture is decoded once and kept", ReferenceEquals(picture, preview.Render(dds)), "");

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string png = Path.Combine(Path.GetTempPath(), "illusion_texture_preview.png");
            using FileStream fs = File.Create(png);
            enc.Save(fs);
            sb.AppendLine($"\ntexture preview ({dds.Name}) -> {png}");

            CheckPreviewIdentity(check, dds, extracted);
        }
    }

    // Two ways the preview can hand back a picture of something else, both found by review after it shipped.
    private static void CheckPreviewIdentity(Action<string, bool, string> check, FileInfo dds, string extracted)
    {
        FileInfo? second = new DirectoryInfo(extracted).GetFiles("*.dds")
            .FirstOrDefault(f => !f.Name.Equals(dds.Name, StringComparison.OrdinalIgnoreCase)
                                 && f.Length != dds.Length);
        if (second == null) return;

        string root = Path.Combine(Path.GetTempPath(), "illusion_preview_identity");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        string one = Path.Combine(root, "archiveA");
        string two = Path.Combine(root, "archiveB");
        Directory.CreateDirectory(one);
        Directory.CreateDirectory(two);

        // Same FILE NAME, different archives, different content — what two archives that both ship a
        // "chrome_d.dds" look like. The texture library answers by bare name over every folder it has ever
        // been told about, so nothing but the preview's own folder handling keeps these apart.
        const string shared = "shared_name.dds";
        var inA = new FileInfo(Path.Combine(one, shared));
        var inB = new FileInfo(Path.Combine(two, shared));
        dds.CopyTo(inA.FullName, true);
        second.CopyTo(inB.FullName, true);
        inA.Refresh();
        inB.Refresh();

        using var preview = new TexturePreviewRenderer();
        string? a = Fingerprint(preview.Render(inA));
        string? b = Fingerprint(preview.Render(inB));
        check("two archives sharing a texture name do not show each other's picture",
            a != null && b != null && a != b, a == b ? "same picture came back twice" : "different");

        // ...and a file replaced under a path already previewed — which is exactly what restoring a backup
        // does, since it deletes the extracted folder and unpacks the older archive to the same paths.
        second.CopyTo(inA.FullName, true);
        inA.Refresh();
        string? afterSwap = Fingerprint(preview.Render(inA));
        check("a texture replaced on disk is not served from the cache",
            afterSwap != null && afterSwap != a, afterSwap == a ? "stale picture" : "re-read");

        Directory.Delete(root, true);
    }

    // Enough of a picture to tell two of them apart: size plus a sample of the pixels.
    private static string? Fingerprint(ImageSource? image)
    {
        if (image is not BitmapSource bmp) return null;
        var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
        long sum = 0;
        for (int i = 0; i < pixels.Length; i += 97) sum += pixels[i] * (i + 1L);
        return $"{bmp.PixelWidth}x{bmp.PixelHeight}:{sum}";
    }

    // Pixels meaningfully brighter than the dark clear color — the lit sphere's footprint.
    private static int CountLitPixels(byte[] bgra)
    {
        int lit = 0;
        for (int i = 0; i < bgra.Length; i += 4)
            if (bgra[i] + bgra[i + 1] + bgra[i + 2] > 140)
                lit++;
        return lit;
    }
}
