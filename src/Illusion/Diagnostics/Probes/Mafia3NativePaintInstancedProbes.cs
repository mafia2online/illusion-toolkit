using System.Numerics;
using System.Security.Cryptography;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintInstancedTransforms(Action<string,bool> check)
    {
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var random=new Random(7811);
        foreach(float blend in new[]{0f,.005f,.01f,.02f,.3f,.5f,.98f,.99f,1f})for(int trial=0;trial<500;trial++)
        {
            var position=new Vector3((float)random.NextDouble()*4000-2000,(float)random.NextDouble()*4000-2000,(float)random.NextDouble()*100);
            if(trial==0)position=new(-0f,0,-0f);
            var normal=Vector3.Normalize(new Vector3((float)random.NextDouble()-.5f,(float)random.NextDouble()-.5f,(float)random.NextDouble()*2-1));
            normal=trial switch{0=>Vector3.UnitZ,1=>-Vector3.UnitZ,2=>Vector3.UnitX,3=>Vector3.UnitY,4=>-Vector3.UnitX,5=>-Vector3.UnitY,
                6=>Vector3.Normalize(new Vector3(1e-9f,0,-1)),7=>Vector3.Normalize(new Vector3(0,1e-9f,-1)),_=>normal};
            var m=CompiledPaintTransform.ComputeInstancedCell(position,normal,.4f,1.7f,blend);
            foreach(float value in new[]{m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43})digest.AppendData(BitConverter.GetBytes(value));
        }
        // Original copied ComputeMatrix and CreateMeshCell post-transform machine code, not a managed self-oracle.
        check("native all-instanced paint matrices match original 54000-component digest",Convert.ToHexString(digest.GetHashAndReset())=="65CFAD6CAFBB8A95A75A5B7CB4C7BE955170908B44C38B198CEE5DC9777D597C");
        var upright=CompiledPaintTransform.ComputeInstancedCell(Vector3.Zero,Vector3.UnitZ,1,1,1);
        check("native all-instanced paint retains upright source Z through fixed axis conversion",Vector3.Distance(Vector3.Transform(Vector3.UnitZ,upright),Vector3.UnitZ)<1e-6f);
        check("native paint template instancing reads Flags independently of HierarchyFlags",new CompiledShaderTemplateInfo(1,0x20000,0).IsInstanced
            &&!new CompiledShaderTemplateInfo(1,0,0x20000).IsInstanced);
    }
}
