using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativeVegetationNoise(Action<string,bool> check)
    {
        var state=new CompiledVegetationNoiseState();var initial=state.Translations;
        state.Step(-1,new(3,4,5));state.Step(0,new(3,4,5));state.Step(1e-8f,new(3,4,5));
        check("Native vegetation noise nonpositive/tiny dt leaves constructor state exact",state.Translations.SequenceEqual(initial));
        var values=new float[120000];int at=0;
        for(int frame=0;frame<10000;frame++)
        {
            if(frame%997==0)state.Reset();
            float dt=frame%11==0?-1:frame%13==0?0:frame%137==0?1000:1/60f;
            state.Step(dt,new(MathF.Sin(frame*.03f)*20,MathF.Cos(frame*.017f)*15,MathF.Sin(frame*.001f)));
            foreach(var v in state.Translations){values[at++]=v.X;values[at++]=v.Y;values[at++]=v.Z;}
        }
        // Original copied Update3DNoise machine code, independently executed with the native constructor,
        // weather context, wraps and resets; no native code or proprietary data is required by this test.
        check("Native vegetation noise original 10000-frame weather/reset digest",Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())))=="4D165441EAD0A07B7AA3189F22C19BB34B9363BF825A2B99A6A93EFA0BB83DFE");
        var before=state.Translations;bool rejected=false;
        try{state.Step(float.NaN,Vector3.Zero);}catch(ArgumentException){rejected=true;}
        check("Native vegetation noise nonfinite request is atomic",rejected&&state.Translations.SequenceEqual(before));
        state.Reset();check("Native vegetation noise reset restores independent constructor vectors",state.Translations.SequenceEqual(initial));
    }
}
