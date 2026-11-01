namespace Illusion.Formats.Compiled;

public sealed partial class CompiledEffectPointPreview
{
    // C_Random::Rand: private MT19937, independent of the emitter Win32 LCG.
    private sealed class PointRandom
    {
        private readonly uint[] _state = new uint[624];
        private int _index = 624;
        public PointRandom(uint seed)
        {
            _state[0] = seed;
            for (int i = 1; i < 624; i++)
                _state[i] = unchecked(1812433253 * (_state[i - 1] ^ (_state[i - 1] >> 30)) + (uint)i);
        }
        public uint Next()
        {
            if (_index == 624)
            {
                for (int i = 0; i < 624; i++)
                {
                    uint y = _state[i] & 0x80000000u | _state[(i + 1) % 624] & 0x7fffffffu;
                    _state[i] = _state[(i + 397) % 624] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908b0dfu : 0);
                }
                _index = 0;
            }
            uint value = _state[_index++];
            value ^= value >> 11;
            value ^= value << 7 & 0x9d2c5680;
            value ^= value << 15 & 0xefc60000;
            value ^= value >> 18;
            return value >> 17;
        }
    }
}
