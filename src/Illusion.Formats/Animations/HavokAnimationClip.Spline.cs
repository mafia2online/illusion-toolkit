using System.Buffers.Binary;
using System.Numerics;

namespace Illusion.Formats.Animations;

public sealed partial class HavokAnimationClip
{
    private static HavokAnimationClip ReadSpline(byte[] bytes, int animation, Func<int, int> pointer,
        string name, float duration, int tracks, short[] mapping)
    {
        var decoder = new SplineDecoder(bytes, animation, pointer, tracks);
        return new HavokAnimationClip(name, duration, tracks, mapping, decoder.Frames,
            seconds => decoder.Sample(seconds / duration * (decoder.Frames - 1)));
    }

    /// <summary>Native packed NURBS track layout. Native signed rotations support 32/40/48/24/16/128-bit modes; scalars support 8/16-bit quantization.</summary>
    private sealed class SplineDecoder
    {
        private readonly byte[] _bytes;
        private readonly int _tracks, _blocks, _maxFrames, _data, _dataEnd, _offsets, _trackOffsets, _maskSize;
        private readonly Dictionary<int, Track[]> _decoded = [];
        public int Frames { get; }
        private sealed record Curve(Vector4[] Points, byte[] Knots, int Degree)
        {
            public Vector4 Sample(float frame)
            {
                if (Points.Length == 1) return Points[0];
                int n = Points.Length - 1;
                frame = Math.Clamp(frame, Knots[Degree], Knots[n + 1]);
                int span = n;
                if (frame < Knots[n + 1])
                    for (int i = Degree; i <= n; i++)
                        if (frame >= Knots[i] && frame < Knots[i + 1]) { span = i; break; }
                Span<Vector4> values = stackalloc Vector4[4];
                for (int j = 0; j <= Degree; j++) values[j] = Points[span - Degree + j];
                for (int r = 1; r <= Degree; r++)
                    for (int j = Degree; j >= r; j--)
                    {
                        int index = span - Degree + j;
                        float denominator = Knots[index + Degree - r + 1] - Knots[index];
                        float alpha = denominator == 0 ? 0 : (frame - Knots[index]) / denominator;
                        values[j] = Vector4.Lerp(values[j - 1], values[j], alpha);
                    }
                return values[Degree];
            }
        }
        private sealed record Track(Curve Translation, Curve Rotation, Curve Scale);
        public SplineDecoder(byte[] bytes, int animation, Func<int, int> pointer, int tracks)
        {
            _bytes = bytes; _tracks = tracks;
            Frames = I(animation + 56); _blocks = I(animation + 60); _maxFrames = I(animation + 64);
            _maskSize = I(animation + 68);
            _data = pointer(animation + 152); int length = I(animation + 160);
            if (Frames is < 2 or > 1000000 || _blocks is < 1 or > 10000 || _maxFrames is < 2 or > 256 || length < 0)
                throw new InvalidDataException("Invalid native spline animation dimensions.");
            Check(_data, length); _dataEnd = checked(_data + length);
            _offsets = pointer(animation + 88);
            int trackOffsetCount = I(animation + 128);
            _trackOffsets = trackOffsetCount == 0 ? 0 : pointer(animation + 120);
            if (I(animation + 96) != _blocks || trackOffsetCount != 0 && trackOffsetCount != checked(_blocks * tracks)
                || _maskSize < tracks * 4)
                throw new InvalidDataException("Inconsistent native spline offset arrays.");
            Check(_offsets, checked(_blocks * 4));
            if (_trackOffsets != 0) Check(_trackOffsets, checked(_blocks * tracks * 4));
        }
        private void Check(int at, int size)
        { if (at < 0 || size < 0 || at > _bytes.Length - size) throw new InvalidDataException("Spline animation extent exceeds its resource."); }
        private int I(int at) { Check(at, 4); return BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(at)); }
        private float F(ref int at)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(at)); at += 4;
            if (!float.IsFinite(value)) throw new InvalidDataException($"Non-finite native spline control point at 0x{at - 4:X}.");
            return value;
        }
        private static int Align(int at, int alignment) => checked((at + alignment - 1) & ~(alignment - 1));
        private void DataCheck(int at, int size)
        { if (at < _data || size < 0 || at > _dataEnd - size) throw new InvalidDataException("Packed spline track exceeds its data array."); }
        private (byte[] Knots, int Degree, int Count) Knots(ref int at)
        {
            DataCheck(at, 3);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at)); int degree = _bytes[at + 2]; at += 3;
            if (degree is < 1 or > 3 || n < degree || n > 65534) throw new InvalidDataException("Unsupported native spline degree/control-point count.");
            int length = checked(n + degree + 2); DataCheck(at, length);
            byte[] knots = _bytes.AsSpan(at, length).ToArray(); at += length;
            for (int i = 1; i < knots.Length; i++) if (knots[i] < knots[i - 1]) throw new InvalidDataException("Native spline knots are not ordered.");
            return (knots, degree, n + 1);
        }
        private Curve Vector(ref int at, byte mask, int quantization, bool scale)
        {
            if (quantization is not (0 or 1)) throw new NotSupportedException("Unsupported native scalar spline quantization.");
            if ((mask & (mask >> 4) & 7) != 0) throw new InvalidDataException("A native scalar component cannot be both static and dynamic.");
            bool dynamic = (mask & 0xf0) != 0;
            var header = dynamic ? Knots(ref at) : (Knots: Array.Empty<byte>(), Degree: 0, Count: 1);
            at = Align(at, 4);
            var fixedValues = new float[3]; var minimum = new float[3]; var maximum = new float[3];
            for (int axis = 0; axis < 3; axis++)
            {
                fixedValues[axis] = scale ? 1 : 0;
                if ((mask & (1 << axis)) != 0) { DataCheck(at, 4); fixedValues[axis] = F(ref at); }
                else if ((mask & (0x10 << axis)) != 0)
                { DataCheck(at, 8); minimum[axis] = F(ref at); maximum[axis] = F(ref at); }
            }
            at = Align(at, 2);
            var points = new Vector4[header.Count];
            Span<float> values = stackalloc float[3];
            for (int point = 0; point < points.Length; point++)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    values[axis] = fixedValues[axis];
                    if ((mask & (0x10 << axis)) == 0) continue;
                    int size = quantization == 0 ? 1 : 2; DataCheck(at, size);
                    uint packed = size == 1 ? _bytes[at] : BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(at)); at += size;
                    values[axis] = minimum[axis] + (maximum[axis] - minimum[axis]) * (packed / (size == 1 ? 255f : 65535f));
                }
                points[point] = new Vector4(values[0], values[1], values[2], 0);
            }
            return new Curve(points, header.Knots, header.Degree);
        }
        private Curve Rotation(ref int at, byte mask, int quantization)
        {
            if (quantization is < 0 or > 5) throw new NotSupportedException("Unsupported native signed quaternion quantization.");
            if (mask == 0) return new Curve([new Vector4(0, 0, 0, 1)], [], 0);
            var header = (mask & 0xf0) != 0 ? Knots(ref at) : (Knots: Array.Empty<byte>(), Degree: 0, Count: 1);
            at = Align(at, quantization switch { 0 or 5 => 4, 2 or 4 => 2, _ => 1 });
            var points = new Vector4[header.Count];
            for (int i = 0; i < points.Length; i++)
            {
                int size = quantization switch { 0 => 4, 1 => 5, 2 => 6, 3 => 3, 4 => 2, _ => 16 }; DataCheck(at, size);
                var bytes = _bytes.AsSpan(at, size);
                points[i] = quantization switch
                {
                    0 => Quaternion32(BinaryPrimitives.ReadUInt32LittleEndian(bytes)),
                    1 => Quaternion40(bytes),
                    _ => HavokQuaternionCodec.Decode(quantization, bytes)
                };
                at += size;
            }
            return new Curve(points, header.Knots, header.Degree);
        }
        private static Vector4 Quaternion32(uint packed)
        {
            uint lattice = packed & 0x3ffff;
            int ring = (int)MathF.Sqrt(lattice);
            float phi = ring * (MathF.PI / (2 * 511));
            float theta = ring == 0 ? 0 : (lattice - ring * ring) * (MathF.PI / (4 * ring));
            float component = ((packed >> 18) & 0x3ff) / 1023f;
            float w = 1 - component * component, radius = MathF.Sqrt(MathF.Max(0, 1 - w * w));
            var result = new Vector4(radius * MathF.Cos(theta) * MathF.Sin(phi), radius * MathF.Sin(theta) * MathF.Sin(phi), radius * MathF.Cos(phi), w);
            if ((packed & (1u << 28)) != 0) result.X = -result.X;
            if ((packed & (1u << 29)) != 0) result.Y = -result.Y;
            if ((packed & (1u << 30)) != 0) result.Z = -result.Z;
            if ((packed & (1u << 31)) != 0) result.W = -result.W;
            return result;
        }
        private static Vector4 Quaternion40(ReadOnlySpan<byte> bytes)
        {
            ulong packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes) | ((ulong)bytes[4] << 32);
            Span<float> values = stackalloc float[4]; int omitted = (int)((packed >> 36) & 3); float sum = 0;
            for (int axis = 0, stored = 0; axis < 4; axis++)
            {
                if (axis == omitted) continue;
                float value = ((int)((packed >> (12 * stored++)) & 0xfff) - 2047) * .0003454356628935784f;
                values[axis] = value; sum += value * value;
            }
            values[omitted] = MathF.Sqrt(MathF.Max(0, 1 - sum)) * ((packed & (1ul << 38)) == 0 ? 1 : -1);
            return new Vector4(values[0], values[1], values[2], values[3]);
        }
        private Track[] Block(int block)
        {
            if (_decoded.TryGetValue(block, out var cached)) return cached;
            int begin = checked(_data + I(_offsets + block * 4)); DataCheck(begin, checked(_tracks * 4));
            var result = new Track[_tracks];
            int sequential = checked(begin + _maskSize);
            for (int i = 0; i < _tracks; i++)
            {
                int mask = begin + i * 4, packed = _bytes[mask];
                int at = _trackOffsets == 0 ? sequential : checked(begin + (I(_trackOffsets + (block * _tracks + i) * 4) & int.MaxValue));
                DataCheck(at, 0);
                var translation = Vector(ref at, _bytes[mask + 1], packed & 3, false); at = Align(at, 4);
                var rotation = Rotation(ref at, _bytes[mask + 2], (packed >> 2) & 15); at = Align(at, 4);
                var scale = Vector(ref at, _bytes[mask + 3], packed >> 6, true);
                sequential = Align(at, 4);
                result[i] = new Track(translation, rotation, scale);
            }
            _decoded.Add(block, result); return result;
        }
        public Matrix4x4[] Sample(float frame)
        {
            int block = Math.Min((int)(frame / (_maxFrames - 1)), _blocks - 1);
            float local = frame - block * (_maxFrames - 1);
            var tracks = Block(block); var result = new Matrix4x4[_tracks];
            for (int i = 0; i < result.Length; i++)
            {
                var translation = tracks[i].Translation.Sample(local); var rotation = tracks[i].Rotation.Sample(local); var scale = tracks[i].Scale.Sample(local);
                var quaternion = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
                if (!float.IsFinite(quaternion.LengthSquared()) || quaternion.LengthSquared() < .000001f) throw new InvalidDataException("Native spline produced an invalid rotation.");
                result[i] = Matrix4x4.CreateScale(scale.X, scale.Y, scale.Z)
                    * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(quaternion))
                    * Matrix4x4.CreateTranslation(translation.X, translation.Y, translation.Z);
            }
            return result;
        }
    }
}
