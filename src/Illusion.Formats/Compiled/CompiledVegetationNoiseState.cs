using System.Numerics;

namespace Illusion.Formats.Compiled;

/// <summary>Four consumed native scene-noise layers. This state does not define GPU wind-history initialization.</summary>
public sealed class CompiledVegetationNoiseState
{
    private static readonly float[] Defaults = [600f, 600f, 600f, 357.48804f, 357.48804f, 357.48804f, 172.30476f, 172.30476f, 172.30476f, 49.48155f, 49.48155f, 49.48155f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 5.474957f, 5.0956745f, 5.522895f, 4.6666327f, 3.9080675f, 4.762508f, 3.049984f, 1.5328537f, 3.2417345f, 6.099968f, 3.0657074f, 0.20018756f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0.4115803f, 0.7859538f, -0.46138722f, -0.69875985f, -0.30621943f, 0.6465016f, -0.44170994f, 0.85591096f, 0.26890272f, 0.59229845f, -0.094078474f, -0.8002073f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 184.71046f, 605.4017f, 152.72754f, 369.42093f, 210.78813f, 305.45508f, 738.84186f, 421.57626f, 610.91016f, 477.66843f, 843.1525f, 221.80515f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f];
    private static readonly float[] OwnVelocityFactors = [.5f, .75f, 2, .75f];
    private static readonly float[] WeatherVelocityFactors = [.5f, .75f, 2, .88f];
    private float[] _state = Defaults.ToArray();
    /// <summary>Restores the four native constructor layers, independent of previous updates.</summary>
    public void Reset() => _state = Defaults.ToArray();
    /// <summary>Independent copy of the four current translation vectors.</summary>
    public Vector3[] Translations => Enumerable.Range(0, 4).Select(i => new Vector3(_state[72+i*3], _state[73+i*3], _state[74+i*3])).ToArray();
    /// <summary>Advances the original noise translation/wrap arithmetic. Nonpositive and tiny steps are native no-ops.</summary>
    public void Step(float dt, Vector3 weatherWind)
    {
        if (!float.IsFinite(dt) || !float.IsFinite(weatherWind.LengthSquared())) throw new ArgumentException("Noise update requires finite time and weather wind.");
        var staged = _state.ToArray();
        Update(staged, dt, 1000, weatherWind, OwnVelocityFactors, WeatherVelocityFactors);
        for (int i=72;i<84;i++) if (!float.IsFinite(staged[i])) throw new ArgumentException("Noise update exceeded finite native arithmetic.");
        _state = staged;
    }
    /// <summary>Native row-major 3x4 matrix rows, four layers; transforms world positions into periodic noise coordinates.</summary>
    public float[] PackMatrices()
    {
        var result=new float[48];
        for(int i=0;i<4;i++)
        {
            var m=Basis(new(_state[24+i*3],_state[25+i*3],_state[26+i*3]),new(_state[i*3],_state[1+i*3],_state[2+i*3]));
            for(int row=0;row<3;row++)
            {
                int at=i*12+row*4, q=row*3;
                result[at]=m[q];result[at+1]=m[q+1];result[at+2]=m[q+2];
                result[at+3]=_state[74+i*3]*m[q+2]+(_state[73+i*3]*m[q+1]+(_state[72+i*3]*m[q]+0));
            }
        }
        return result;
    }
private static void Update(float[]p,float dt,float limit,Vector3 wind,float[]a,float[]b){if(dt<=1e-8f)return;for(int i=0;i<4;i++){var v=new Vector3(p[48+i*3],p[49+i*3],p[50+i*3]);float x=p[72+i*3]-(dt*v.X)*a[i];float y=p[73+i*3]-(dt*v.Y)*a[i];float z=p[74+i*3]-(dt*v.Z)*a[i];x-=(wind.X*dt)*b[i];y-=(wind.Y*dt)*b[i];z-=(wind.Z*dt)*b[i];float size=(x*x+y*y)+z*z;if(size>limit*limit){var q=Basis(new(p[24+i*3],p[25+i*3],p[26+i*3]),new(p[i*3],p[1+i*3],p[2+i*3]));float fx=MathF.Floor(z*q[2]+(y*q[1]+(x*q[0]+0))),fy=MathF.Floor(z*q[5]+(y*q[4]+(x*q[3]+0))),fz=MathF.Floor(z*q[8]+(y*q[7]+(x*q[6]+0)));var inv=Inverse(q);float tx=-((inv[0]*0+inv[1]*0)+inv[2]*0),ty=-((inv[3]*0+inv[4]*0)+inv[5]*0),tz=-((inv[6]*0+inv[7]*0)+inv[8]*0);x-=tx+(fz*inv[2]+(fy*inv[1]+fx*inv[0]));y-=ty+(fz*inv[5]+(fy*inv[4]+fx*inv[3]));z-=tz+(fz*inv[8]+(fy*inv[7]+fx*inv[6]));}p[72+i*3]=x;p[73+i*3]=y;p[74+i*3]=z;}}
private static float[] Basis(Vector3 r,Vector3 scale){float sx=MathF.Sin(r.X),cx=MathF.Cos(r.X),sy=MathF.Sin(r.Y),cy=MathF.Cos(r.Y),sz=MathF.Sin(r.Z),cz=MathF.Cos(r.Z);float[]m=[cy*cz,sz,-sy*cz,sx*sy-cy*(cx*sz),cx*cz,cx*sy*sz+sx*cy,cx*sy+cy*(sx*sz),-sx*cz,cx*cy-sx*sy*sz];float[]inv=[1/scale.X,1/scale.Y,1/scale.Z];for(int i=0;i<9;i++)m[i]*=inv[i%3];return m;}
private static float[] Inverse(float[]m){float c0=m[4]*m[8]-m[7]*m[5],c1=m[1]*m[8]-m[7]*m[2],c2=m[1]*m[5]-m[4]*m[2];float det=(m[0]*c0-m[3]*c1)+m[6]*c2;if(Math.Abs(det)<1e-16f)return[1,0,0,0,1,0,0,0,1];float r=1/det,n=-r;return[c0*r,c1*n,c2*r,(m[3]*m[8]-m[5]*m[6])*n,(m[0]*m[8]-m[2]*m[6])*r,(m[0]*m[5]-m[2]*m[3])*n,(m[3]*m[7]-m[4]*m[6])*r,(m[0]*m[7]-m[1]*m[6])*n,(m[0]*m[4]-m[1]*m[3])*r];}
}
