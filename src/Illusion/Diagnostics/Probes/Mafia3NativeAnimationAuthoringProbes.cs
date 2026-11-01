using System.IO;
using System.Numerics;
using System.Reflection;
using System.Xml.Linq;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Animations;
using Illusion.Formats.Archive;
using Illusion.Formats;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativeAnimationAuthoring(Action<string, bool> check)
    {
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "tables", "ingame.sds")); if (!archive.Exists) return;
        string folder = SdsMeshLoader.EnsureExtracted(archive); string source = SdsManifest.Load(folder).GetFiles("hkAnimation").First();
        var original = File.ReadAllBytes(source); string manifest = Path.Combine(folder, "SDSContent.xml"); var originalManifest = File.ReadAllBytes(manifest);
        var document = new HavokAnimationDocument(source); var clip = document.Clips[0]; int frame = clip.FrameCount / 2;
        var originalKeys = HavokAnimationAuthoring.Capture(clip); float time = clip.Duration * frame / (clip.FrameCount - 1);
        var key = originalKeys[frame].Transforms[0] * Matrix4x4.CreateTranslation(.25f, 0, 0);
        IEditAction? edit = null, second = null;
        var fault = typeof(HavokAnimationDocument).GetProperty("SaveAfterNativeWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            bool refused = false;
            var invalid = Matrix4x4.Identity; invalid.M14 = .5f;
            try { document.SetTransformFrame(0, frame, 0, invalid); } catch (NotSupportedException) { refused = true; }
            check("native animation authoring refuses invalid TRS before changing document bytes", refused && !document.IsDirty && document.ToArray().AsSpan().SequenceEqual(original));
            edit = document.SetTransformFrame(0, frame, 0, key); var changed = document.ToArray(); var rewritten = document.Clips[0];
            check("native spline animation becomes a real native interleaved clip with unchanged binding identities", rewritten.FrameCount == clip.FrameCount && rewritten.TrackCount == clip.TrackCount
                && rewritten.Duration == clip.Duration && rewritten.BlendHint == clip.BlendHint && rewritten.SkeletonName == clip.SkeletonName
                && rewritten.TrackToBone.SequenceEqual(clip.TrackToBone) && rewritten.FloatTrackToSlot.SequenceEqual(clip.FloatTrackToSlot)
                && Vector3.Distance(rewritten.Sample(time)[0].Translation, key.Translation) < .00001f);
            float maximum = 0;
            for (int sample = 0; sample < clip.FrameCount; sample++)
            {
                var pose = rewritten.Sample(clip.Duration * sample / (clip.FrameCount - 1));
                for (int track = 0; track < clip.TrackCount; track++)
                {
                    if (sample == frame && track == 0) continue;
                    maximum = Math.Max(maximum, Vector3.Distance(pose[track].Translation, originalKeys[sample].Transforms[track].Translation));
                    maximum = Math.Max(maximum, MathF.Abs(pose[track].M11 - originalKeys[sample].Transforms[track].M11));
                }
            }
            check("native interleaved bake retains every unedited original frame track within TRS precision", maximum < .0001f);
            var exactKey = document.ReadFrame(0, frame).Transforms[Math.Min(17, clip.TrackCount - 1)];
            check("native animation authoring reads exact key matrices and applies loaded keys byte-exactly", HavokAnimationAuthoring.SetTransformFrame(changed, 0, frame, Math.Min(17, clip.TrackCount - 1), exactKey)
                .AsSpan().SequenceEqual(changed));
            second = document.SetTransformFrame(0, frame, 0, key * Matrix4x4.CreateTranslation(.125f, 0, 0));
            var secondBytes = document.ToArray();
            check("repeated native key edits touch only their native sample without growth or unrelated-key resampling", secondBytes.Length == changed.Length
                && secondBytes.Where((value, index) => value != changed[index]).Count() <= 48);
            refused = false; try { edit.Undo(); } catch (EditRefusedException) { refused = true; }
            check("native clip history refuses stale action before overwriting a later authored key", refused && Vector3.Distance(document.Clips[0].Sample(time)[0].Translation, (key * Matrix4x4.CreateTranslation(.125f, 0, 0)).Translation) < .00001f);
            second.Undo(); second = null;
            fault.SetValue(document, (Action)(() => throw new IOException("Injected native animation save failure.")));
            refused = false; try { document.SaveWorkingCopy(); } catch (IOException) { refused = true; } finally { fault.SetValue(document, null); }
            check("native animation save failure restores both payload and SDS manifest while retaining pending history", refused && document.IsDirty
                && File.ReadAllBytes(source).AsSpan().SequenceEqual(original) && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(originalManifest));
            document.SaveWorkingCopy(); var fresh = new HavokAnimationDocument(source);
            check("native authored key persists through independent working-copy reload", !fresh.IsDirty && Vector3.Distance(fresh.Clips[0].Sample(time)[0].Translation, key.Translation) < .00001f);
            static XElement Entry(byte[] xml, string path)
            { using var stream = new MemoryStream(xml, writable: false); return XElement.Load(stream).Elements("ResourceEntry").Single(entry => entry.Element("File")?.Value == Path.GetFileName(path)); }
            var oldEntry = Entry(originalManifest, source); var newEntry = Entry(File.ReadAllBytes(manifest), source); long growth = changed.LongLength - original.LongLength;
            check("native animation SDS reservation follows actual additional packfile bytes only in OtherRam", (long)newEntry.Attribute("OtherRam")! - (long)oldEntry.Attribute("OtherRam")! == growth
                && newEntry.Attribute("SlotRam")!.Value == oldEntry.Attribute("SlotRam")!.Value && newEntry.Attribute("SlotVram")!.Value == oldEntry.Attribute("SlotVram")!.Value);
            var packed = SdsArchive.Pack(folder, MafiaEnvironment.Profile); using var compressed = new MemoryStream(); packed.Save(compressed, new SdsWriteOptions { Compress = true });
            compressed.Position = 0; var freshArchive = SdsArchive.Load(compressed);
            int resourceIndex = int.Parse(Path.GetFileName(source).Split('_')[1], System.Globalization.CultureInfo.InvariantCulture);
            check("native authored clip and exact resource RAM survive compressed SDS serialization and independent decode", freshArchive.Entries[resourceIndex].Data!.AsSpan().SequenceEqual(changed)
                && freshArchive.Entries[resourceIndex].OtherRamRequired == (uint)newEntry.Attribute("OtherRam")!
                && Vector3.Distance(HavokAnimationClip.Read(freshArchive.Entries[resourceIndex].Data!)[0].Sample(time)[0].Translation, key.Translation) < .00001f);
            edit.Undo(); edit.Redo(); edit.Undo(); edit = null; document.SaveWorkingCopy();
            check("native key undo redo restores exact original packfile and complete resource manifest", File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
                && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(originalManifest));
        }
        finally { fault.SetValue(document, null); second?.Undo(); edit?.Undo(); document.SaveWorkingCopy(); }

        string siblingPath = SdsManifest.Load(folder).GetFiles("hkAnimation").Skip(1).First(); var siblingOriginal = File.ReadAllBytes(siblingPath);
        var batchFirst = new HavokAnimationDocument(source); var batchSecond = new HavokAnimationDocument(siblingPath);
        IEditAction? firstBatchEdit = null, secondBatchEdit = null;
        try
        {
            firstBatchEdit = batchFirst.SetTransformFrame(0, 1, 0, batchFirst.Clips[0].Sample(batchFirst.Clips[0].Duration / (batchFirst.Clips[0].FrameCount - 1))[0] * Matrix4x4.CreateTranslation(.1f, 0, 0));
            secondBatchEdit = batchSecond.SetFloatFrame(0, 2, 0, 145.5f);
            fault.SetValue(batchSecond, (Action)(() => throw new IOException("Injected second animation resource save failure.")));
            bool refused = false; try { HavokAnimationDocument.SaveWorkingCopies([batchFirst, batchSecond]); } catch (IOException) { refused = true; } finally { fault.SetValue(batchSecond, null); }
            check("native animation batch save rolls back an earlier file when the second resource write fails", refused && batchFirst.IsDirty && batchSecond.IsDirty
                && File.ReadAllBytes(source).AsSpan().SequenceEqual(original) && File.ReadAllBytes(siblingPath).AsSpan().SequenceEqual(siblingOriginal)
                && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(originalManifest));
            HavokAnimationDocument.SaveWorkingCopies([batchFirst, batchSecond]);
            check("native animation sibling files share one manifest transaction and independent budget provenance", !batchFirst.IsDirty && !batchSecond.IsDirty
                && new HavokAnimationDocument(source).ToArray().AsSpan().SequenceEqual(batchFirst.ToArray())
                && new HavokAnimationDocument(siblingPath).ToArray().AsSpan().SequenceEqual(batchSecond.ToArray()));
            firstBatchEdit.Undo(); secondBatchEdit.Undo(); firstBatchEdit = secondBatchEdit = null;
            HavokAnimationDocument.SaveWorkingCopies([batchFirst, batchSecond]);
            check("native multi-file animation history restores every original payload and the exact shared manifest", File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
                && File.ReadAllBytes(siblingPath).AsSpan().SequenceEqual(siblingOriginal) && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(originalManifest));
        }
        finally { fault.SetValue(batchSecond, null); secondBatchEdit?.Undo(); firstBatchEdit?.Undo(); HavokAnimationDocument.SaveWorkingCopies([batchFirst, batchSecond]); }

        var expressionArchive = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cutscenes", "bios", "fmv_bio02_passion_cassandra_03.sds")); if (!expressionArchive.Exists) return;
        folder = SdsMeshLoader.EnsureExtracted(expressionArchive);
        source = SdsManifest.Load(folder).GetFiles("hkAnimation").First(path => HavokAnimationClip.Read(File.ReadAllBytes(path)).Any(candidate => candidate.SkeletonName == "prod_female"));
        original = File.ReadAllBytes(source); manifest = Path.Combine(folder, "SDSContent.xml"); originalManifest = File.ReadAllBytes(manifest);
        var facial = new HavokAnimationDocument(source); int index = facial.Clips.Select((candidate, ordinal) => (candidate, ordinal)).Single(pair => pair.candidate.SkeletonName == "prod_female").ordinal;
        var nativeClip = facial.Clips[index]; const int floatFrame = 255, floatTrack = 0; float floatTime = nativeClip.Duration * floatFrame / (nativeClip.FrameCount - 1);
        IEditAction? expression = null;
        try
        {
            expression = facial.SetFloatFrame(index, floatFrame, floatTrack, 173.25f);
            check("native expression authoring preserves raw percentage float keys without clamping overshoot", MathF.Abs(facial.Clips[index].SampleFloats(floatTime)[floatTrack] - 173.25f) < .001f
                && facial.Clips[index].FloatTrackToSlot.SequenceEqual(nativeClip.FloatTrackToSlot) && facial.Clips[index].FloatTrackCount == 154);
            check("native expression authoring reads the exact stored key rather than time-rounded interpolation", facial.ReadFrame(index, floatFrame).Floats[floatTrack] == 173.25f);
            var firstExpression = facial.ToArray(); var signed = facial.SetFloatFrame(index, floatFrame, floatTrack, -12.75f); var signedBytes = facial.ToArray();
            check("native authored expression edits preserve all other keys and accept finite signed values", signedBytes.Length == firstExpression.Length
                && signedBytes.Where((value, ordinal) => value != firstExpression[ordinal]).Count() <= 4
                && MathF.Abs(facial.Clips[index].SampleFloats(floatTime)[floatTrack] + 12.75f) < .001f);
            signed.Undo();
            facial.SaveWorkingCopy(); var fresh = new HavokAnimationDocument(source);
            check("native expression keys persist with actual named float-slot binding after reload", MathF.Abs(fresh.Clips[index].SampleFloats(floatTime)[floatTrack] - 173.25f) < .001f
                && fresh.Clips[index].SkeletonName == nativeClip.SkeletonName && fresh.Clips[index].TrackToBone.SequenceEqual(nativeClip.TrackToBone));
            expression.Undo(); expression.Redo(); expression.Undo(); expression = null; facial.SaveWorkingCopy();
            check("native expression undo restores exact original packed spline and native manifest", File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
                && File.ReadAllBytes(manifest).AsSpan().SequenceEqual(originalManifest));
        }
        finally { expression?.Undo(); facial.SaveWorkingCopy(); }
    }
}
