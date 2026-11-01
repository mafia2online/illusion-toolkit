using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativeWaterTemporalViewport(Action<string, bool> check)
    {
        string directory = Path.GetFullPath(Path.Combine(".scratch", "probe-native-water-temporal"));
        Directory.CreateDirectory(directory);
        byte[] Dds(byte r, byte g, byte b)
        {
            var data = new byte[132];
            void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
            U(0, 0x20534444); U(4, 124); U(8, 0x100f); U(12, 1); U(16, 1); U(20, 4); U(76, 32); U(80, 0x41); U(88, 32);
            U(92, 0xff0000); U(96, 0xff00); U(100, 0xff); U(104, 0xff000000); U(108, 0x1000);
            data[128] = b; data[129] = g; data[130] = r; data[131] = 255; return data;
        }
        string Save(string name, byte r, byte g, byte b) { string p = Path.Combine(directory, name + ".dds"); File.WriteAllBytes(p, Dds(r,g,b)); return p; }
        string color = Save("color", 10, 80, 35), mask = Save("mask", 255, 0, 0), red = Save("red", 255, 0, 0), blue = Save("blue", 0, 0, 255);
        var lookup = new byte[128]; for (int i=0;i<64;i++) { lookup[i*2]=(byte)((MathF.Sin(2*MathF.PI*(i+.5f)/64)*.5f+.5f)*255); lookup[i*2+1]=(byte)((MathF.Cos(2*MathF.PI*(i+.5f)/64)*.5f+.5f)*255); }
        var waves = new float[128]; waves[4]=.5f; waves[8]=.15915493667125702f;
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = RenderMode.MaterialPreview };
        using var target = new SharedRenderTarget(gpu, 192, 128);
        renderer.Textures.AddFolder(directory); renderer.Camera.LookAt(new(0,-5,3), Vector3.Zero);
        MeshData Plane(string name, Vector3[] points, string texture, bool water = false) => new()
        {
            Name=name, World=Matrix4x4.Identity, Positions=points,
            Normals=Enumerable.Repeat(Vector3.UnitZ,4).ToArray(), Tangents=Enumerable.Repeat(Vector3.UnitX,4).ToArray(), Binormals=Enumerable.Repeat(Vector3.UnitY,4).ToArray(),
            UVs=[Vector2.Zero,Vector2.UnitX,Vector2.One,Vector2.UnitY], Indices=[0,1,2,0,2,3],
            Parts=[new MeshPart(0,6,texture,surface:water ? CompiledSurfaceMaterials.WaterDefinition with { NativeWaterDefinition=new(color,mask) } : null)]
        };
        renderer.AddMesh(Plane("water",[new(-4,-4,0),new(4,-4,0),new(4,4,0),new(-4,4,0)],color,true));
        renderer.AddMesh(Plane("reflection-wall",[new(-4,3,0),new(4,3,0),new(4,3,4),new(-4,3,4)],red));
        renderer.AddMesh(Plane("underwater-floor",[new(-5,-5,-.04f),new(5,-5,-.04f),new(5,5,-.04f),new(-5,5,-.04f)],blue));
        renderer.ConfigureNativeWater(waves, new ReadOnlyMemory<byte>[] {lookup});
        byte[] Draw(SharedRenderTarget? output=null) { renderer.Render(output??target); return RenderTargetReadback.Read(gpu,output??target); }
        check("temporal viewport requires optics input before activation", !renderer.NativeWaterTemporalReflections && !renderer.NativeWaterTemporalReflectionsActive);
        check("temporal viewport loads explicit toolkit SSR BRDF boundary", renderer.ConfigureNativeWaterOptics(Dds(0,255,0),new(.01f,.1f)));
        renderer.NativeWaterReflections=true; renderer.NativeWaterRefraction=false;
        byte[] baseline=Draw();
        renderer.NativeWaterTemporalReflections=true;
        byte[] first=Draw();
        check("temporal viewport executes native precision reflection path", renderer.NativeWaterTemporalReflectionsActive && renderer.NativeWaterTemporalReflectionFrames==1 && !first.SequenceEqual(baseline));
        byte[] second=Draw();
        check("temporal viewport advances separate radiance confidence history", renderer.NativeWaterTemporalReflectionFrames==2);
        renderer.ResetNativeWaterTemporalReflections();
        check("temporal viewport reset restores first-frame pixels exactly", Draw().SequenceEqual(first) && renderer.NativeWaterTemporalReflectionFrames==1);
        check("temporal viewport replay restores second-frame pixels exactly", Draw().SequenceEqual(second));
        renderer.NativeWaterTemporalReflections=false;
        check("temporal viewport disabled restores exact bounded SSR baseline", Draw().SequenceEqual(baseline) && !renderer.NativeWaterTemporalReflectionsActive);
        renderer.NativeWaterTemporalReflections=true;
        renderer.Camera.Position+=new Vector3(20,0,0); Draw();
        check("temporal viewport camera cut restarts native history", renderer.NativeWaterTemporalReflectionFrames==1);
        renderer.Camera.Position-=new Vector3(20,0,0);
        check("temporal viewport returning camera resets exact original first frame", Draw().SequenceEqual(first) && renderer.NativeWaterTemporalReflectionFrames==1);
        renderer.Camera.Yaw += MathF.PI; Draw();
        check("temporal viewport same-position half-turn resets history", renderer.NativeWaterTemporalReflectionFrames==1);
        renderer.Camera.Yaw -= MathF.PI; Draw();
        float originalFov = renderer.Camera.Fov; renderer.Camera.Fov += .2f; Draw();
        check("temporal viewport projection cut resets history", renderer.NativeWaterTemporalReflectionFrames==1);
        renderer.Camera.Fov = originalFov;
        check("temporal viewport projection restore starts exact first-frame output", Draw().SequenceEqual(first) && renderer.NativeWaterTemporalReflectionFrames==1);
        renderer.NativeWaterReflections=false; Draw();
        check("temporal viewport reflection disable releases active history", !renderer.NativeWaterTemporalReflectionsActive && renderer.NativeWaterTemporalReflectionFrames==0);
    }
}
