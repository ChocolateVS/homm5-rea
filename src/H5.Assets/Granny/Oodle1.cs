// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// C# port of the Oodle1 decompressor from libopengrn (https://github.com/arves100/opengr2,
// oodle1.c / compression.c, MPL-2.0), itself derived from nwn2mdk's gr2_decompress.cpp
// (https://github.com/Arbos/nwn2mdk). Only this file carries the MPL-2.0 licence.

namespace H5.Assets.Granny;

/// <summary>
/// Decompressor for Granny 2 "Oodle1" sections (compression type 2, also accepted for
/// type 1). A section is split in three runs at oodleStop0/oodleStop1/decompressedLength,
/// each decoded with its own adaptive dictionary; all three share one arithmetic decoder.
/// </summary>
public static class Oodle1
{
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int decompressedLength, int stop0, int stop1)
    {
        var output = new byte[decompressedLength];
        if (compressed.Length == 0) return output;

        // Three 12-byte parameter blocks precede the bit stream. The decoder may read up to
        // a couple of bytes past the end, so pad the stream with zeros.
        var stream = new byte[compressed.Length + 8];
        compressed.CopyTo(stream);
        var parameters = new Parameter[3];
        for (int i = 0; i < 3; i++) parameters[i] = Parameter.Read(stream, i * 12);

        var decoder = new Decoder(stream, 36);
        int[] steps = { stop0, stop1, decompressedLength };
        int pos = 0;
        for (int i = 0; i < 3; i++)
        {
            var dict = new Dictionary(parameters[i]);
            while (pos < steps[i])
                pos += dict.DecompressBlock(ref decoder, output, pos);
        }
        return output;
    }

    struct Parameter
    {
        public uint DecodedValueMax, BackrefValueMax, DecodedCount, HighbitCount;
        public byte[] SizesCount;

        public static Parameter Read(byte[] d, int o)
        {
            uint w0 = BitConverter.ToUInt32(d, o), w1 = BitConverter.ToUInt32(d, o + 4);
            return new Parameter
            {
                DecodedValueMax = w0 & 0x1FF,
                BackrefValueMax = w0 >> 9,
                DecodedCount = w1 & 0x1FF,
                HighbitCount = w1 >> 19,
                SizesCount = new[] { d[o + 8], d[o + 9], d[o + 10], d[o + 11] },
            };
        }
    }

    struct Decoder
    {
        uint _numer, _denom, _nextDenom;
        readonly byte[] _s;
        int _p;

        public Decoder(byte[] s, int p)
        {
            _s = s; _p = p;
            _numer = (uint)(s[p] >> 1);
            _denom = 0x80;
            _nextDenom = 0;
        }

        public ushort Decode(ushort max)
        {
            for (; _denom <= 0x800000; _denom <<= 8)
            {
                _numer <<= 8;
                _numer |= (uint)((_s[_p] << 7) & 0x80);
                _numer |= (uint)((_s[_p + 1] >> 1) & 0x7f);
                _p++;
            }
            _nextDenom = _denom / max;
            return (ushort)Math.Min(_numer / _nextDenom, (uint)(max - 1));
        }

        public ushort Commit(ushort max, ushort val, ushort err)
        {
            _numer -= _nextDenom * val;
            if (val + err < max) _denom = _nextDenom * err;
            else _denom -= _nextDenom * val;
            return val;
        }

        public ushort DecodeCommit(ushort max) => Commit(max, Decode(max), 1);
    }

    sealed class WeighWindow
    {
        readonly ushort _countCap;
        ushort[] _ranges;
        readonly List<ushort> _values = new();
        readonly List<ushort> _weights = new();
        ushort _weightTotal;
        ushort _threshIncrease, _threshIncreaseCap, _threshRangeRebuild, _threshWeightRebuild;

        public List<ushort> Values => _values;

        public WeighWindow(uint maxValue, uint countCap)
        {
            _weightTotal = 4;
            _countCap = (ushort)(countCap + 1);
            _ranges = new ushort[] { 0, 0x4000 };
            _weights.Add(4);
            _values.Add(0);
            _threshIncrease = 4;
            _threshRangeRebuild = 8;
            _threshWeightRebuild = (ushort)Math.Max(256u, Math.Min(32 * maxValue, 15160u));
            _threshIncreaseCap = maxValue > 64
                ? (ushort)Math.Min(2 * maxValue, _threshWeightRebuild / 2u - 32u)
                : (ushort)128;
        }

        void RebuildRanges()
        {
            if (_ranges.Length != _weights.Count + 1)
                _ranges = new ushort[_weights.Count + 1];
            ushort rangeWeight = (ushort)(8 * 0x4000 / _weightTotal);
            ushort rangeStart = 0;
            for (int i = 0; i < _weights.Count; i++)
            {
                _ranges[i] = rangeStart;
                rangeStart = (ushort)(rangeStart + (_weights[i] * rangeWeight) / 8);
            }
            _ranges[^1] = 0x4000;

            if (_threshIncrease > _threshIncreaseCap / 2)
                _threshRangeRebuild = (ushort)(_weightTotal + _threshIncreaseCap);
            else
            {
                _threshIncrease = (ushort)(_threshIncrease * 2);
                _threshRangeRebuild = (ushort)(_weightTotal + _threshIncrease);
            }
        }

        void RebuildWeights()
        {
            ushort total = 0;
            for (int i = 0; i < _weights.Count; i++)
            {
                _weights[i] = (ushort)(_weights[i] / 2);
                total += _weights[i];
            }
            _weightTotal = total;

            for (int i = 1; i < _weights.Count; i++)
            {
                while (i < _weights.Count && _weights[i] == 0)
                {
                    _weights[i] = _weights[^1];
                    _values[i] = _values[^1];
                    _weights.RemoveAt(_weights.Count - 1);
                    _values.RemoveAt(_values.Count - 1);
                }
            }

            // max_element over [1, count): first index holding the largest weight, if any > 0.
            int it = 1;
            ushort best = 0;
            for (int i = 1; i < _weights.Count; i++)
                if (_weights[i] > best) { best = _weights[i]; it = i; }
            if (it < _weights.Count)
            {
                int last = _weights.Count - 1;
                (_weights[it], _weights[last]) = (_weights[last], _weights[it]);
                int lastV = _values.Count - 1;
                (_values[it], _values[lastV]) = (_values[lastV], _values[it]);
            }

            if (_weights.Count < _countCap && _weights[0] == 0)
            {
                _weights[0] = 1;
                _weightTotal++;
            }
        }

        /// <summary>Returns (newIndex, 0) when a new symbol must be read, else (-1, value).</summary>
        public (int Index, ushort Value) TryDecode(ref Decoder decoder)
        {
            if (_weightTotal >= _threshRangeRebuild)
            {
                if (_threshRangeRebuild >= _threshWeightRebuild) RebuildWeights();
                RebuildRanges();
            }

            ushort value = decoder.Decode(0x4000);
            int rangeIt = _ranges.Length - 1;
            for (int i = 0; i < _ranges.Length; i++)
                if (_ranges[i] > value) { rangeIt = i; break; }
            rangeIt--;

            decoder.Commit(0x4000, _ranges[rangeIt], (ushort)(_ranges[rangeIt + 1] - _ranges[rangeIt]));

            int index = rangeIt;
            _weights[index]++;
            _weightTotal++;

            if (index > 0) return (-1, _values[index]);

            if (_weights.Count >= _ranges.Length && decoder.DecodeCommit(2) == 1)
            {
                int idx = _ranges.Length + decoder.DecodeCommit((ushort)(_weights.Count - _ranges.Length + 1)) - 1;
                _weights[idx] += 2;
                _weightTotal += 2;
                return (-1, _values[idx]);
            }

            _values.Add(0);
            _weights.Add(2);
            _weightTotal += 2;
            if (_weights.Count == _countCap)
            {
                _weightTotal -= _weights[0];
                _weights[0] = 0;
            }
            return (_values.Count - 1, 0);
        }
    }

    sealed class Dictionary
    {
        static readonly uint[] LongSizes = { 128u, 192u, 256u, 512u };

        uint _decodedSize, _backrefSize;
        readonly uint _decodedValueMax, _backrefValueMax, _lowbitValueMax, _midbitValueMax, _highbitValueMax;
        readonly WeighWindow _lowbit, _highbit;
        readonly WeighWindow[] _midbit, _decoded, _sizes;

        public Dictionary(Parameter p)
        {
            _decodedValueMax = p.DecodedValueMax;
            _backrefValueMax = p.BackrefValueMax;
            _lowbitValueMax = Math.Min(_backrefValueMax + 1, 4);
            _midbitValueMax = Math.Min(_backrefValueMax / 4 + 1, 256);
            _highbitValueMax = _backrefValueMax / 1024 + 1;

            _lowbit = new WeighWindow(_lowbitValueMax - 1, _lowbitValueMax);
            _highbit = new WeighWindow(_highbitValueMax - 1, p.HighbitCount + 1);
            _midbit = new WeighWindow[_highbitValueMax];
            for (int i = 0; i < _midbit.Length; i++) _midbit[i] = new WeighWindow(_midbitValueMax - 1, _midbitValueMax);
            _decoded = new WeighWindow[4];
            for (int i = 0; i < 4; i++) _decoded[i] = new WeighWindow(_decodedValueMax - 1, p.DecodedCount);
            _sizes = new WeighWindow[4 * 16 + 1];
            int k = 0;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 16; j++)
                    _sizes[k++] = new WeighWindow(64, p.SizesCount[3 - i]);
            _sizes[k] = new WeighWindow(64, p.SizesCount[0]);
        }

        static ushort Read(WeighWindow w, ref Decoder d, ushort max)
        {
            var (index, value) = w.TryDecode(ref d);
            if (index >= 0) value = w.Values[index] = d.DecodeCommit(max);
            return value;
        }

        public int DecompressBlock(ref Decoder d, byte[] output, int pos)
        {
            _backrefSize = Read(_sizes[_backrefSize], ref d, 65);

            if (_backrefSize > 0)
            {
                uint size = _backrefSize < 61u ? _backrefSize + 1 : LongSizes[_backrefSize - 61u];
                uint range = Math.Min(_backrefValueMax, _decodedSize);

                ushort low = Read(_lowbit, ref d, (ushort)_lowbitValueMax);
                ushort high = Read(_highbit, ref d, (ushort)(range / 1024u + 1));
                ushort mid = Read(_midbit[high], ref d, (ushort)Math.Min(range / 4 + 1, 256));

                int offset = (high << 10) + (mid << 2) + low + 1;
                _decodedSize += size;
                int n = (int)Math.Min(size, (uint)(output.Length - pos));
                for (int i = 0; i < n; i++) output[pos + i] = output[pos + i - offset];
                return (int)size;
            }

            ushort v = Read(_decoded[pos % 4], ref d, (ushort)_decodedValueMax);
            output[pos] = (byte)v;
            _decodedSize++;
            return 1;
        }
    }
}
