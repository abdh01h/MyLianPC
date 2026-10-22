// TinyUZ stream encoder/decoder used by the HydroShift II ring-lighting upload (command 0xFC).
// Ported from hydroshift2-rs/src/compression.rs (MIT, (c) 2026 andyjusa), which implements the
// format of https://github.com/sisong/tinyuz (MIT). See THIRD-PARTY-NOTICES.md.
namespace MyLianPC.Hardware.LianLi;

public static class TinyUz
{
    private const int MinLiteralLine = 15;
    private const int MaxDictDistance = 127;

    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        var w = new Writer();
        int pos = 0;
        while (pos < data.Length)
        {
            var (len, dist) = LongestMatch(data, pos);
            if (len >= 3)
            {
                w.Match(len, dist);
                pos += len;
                continue;
            }
            int start = pos++;
            while (pos < data.Length && LongestMatch(data, pos).Length < 3) pos++;
            w.Data(data[start..pos]);
        }
        return w.Finish();
    }

    private static (int Length, int Distance) LongestMatch(ReadOnlySpan<byte> data, int pos)
    {
        (int, int) best = (0, 0);
        for (int dist = 1; dist <= Math.Min(pos, MaxDictDistance); dist++)
        {
            int len = 0;
            while (pos + len < data.Length && data[pos + len] == data[pos + len - dist]) len++;
            if (len > best.Item1) best = (len, dist);
        }
        return best;
    }

    private sealed class Writer
    {
        private readonly List<byte> _code = [];
        private int _typeCount, _typesIndex, _dictPosBack = 1, _maxDictSize = 1;
        private bool _haveDataBack;

        private void Type(int bit)
        {
            if (_typeCount == 0) { _typesIndex = _code.Count; _code.Add(0); }
            _code[_typesIndex] |= (byte)((bit & 1) << _typeCount);
            _typeCount = (_typeCount + 1) % 8;
        }

        private void Len(int value, int packBits)
        {
            int count = 1, reduced = value;
            while (reduced >= 1 << (count * packBits)) { reduced -= 1 << (count * packBits); count++; }
            while (count > 0)
            {
                count--;
                for (int bit = 0; bit < packBits; bit++) Type((reduced >> (count * packBits + bit)) & 1);
                Type(count > 0 ? 1 : 0);
            }
        }

        private void DictPos(int pos)
        {
            bool extended = pos >= 128;
            if (extended) pos -= 128;
            _code.Add((byte)((pos & 0x7F) | (extended ? 0x80 : 0)));
            if (extended) Len(pos >> 7, 2);
        }

        private void Control(int control)
        {
            Type(0);
            Len(control, 1);
            if (_haveDataBack) Type(0);
            DictPos(0);
        }

        public void Data(ReadOnlySpan<byte> data)
        {
            if (data.Length >= MinLiteralLine)
            {
                Control(1);
                Len(data.Length - MinLiteralLine, 2);
                foreach (var b in data) _code.Add(b);
            }
            else
            {
                foreach (var b in data) { Type(1); _code.Add(b); }
            }
            _haveDataBack = true;
        }

        public void Match(int length, int distance)
        {
            Type(0);
            _maxDictSize = Math.Max(_maxDictSize, distance);
            bool saved = _dictPosBack == distance && _haveDataBack;
            Len(length - 2, 1);
            if (_haveDataBack) Type(saved ? 1 : 0);
            if (!saved) DictPos(distance);
            _haveDataBack = false;
            _dictPosBack = distance;
        }

        public byte[] Finish()
        {
            Control(3);
            return [.. BitConverter.GetBytes((uint)_maxDictSize), .. _code];
        }
    }

    /// <summary>Reference decoder, used to verify every stream before it is sent to the device.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> code, int maxOutput)
    {
        if (code.Length < 5) throw new InvalidDataException("TinyUZ stream is too short.");
        int dictSize = (int)BitConverter.ToUInt32(code[..4]);
        if (dictSize == 0) throw new InvalidDataException("Invalid TinyUZ dictionary size.");
        var r = new Reader(code.ToArray());
        var output = new List<byte>();
        int dictPosBack = 1;
        bool haveDataBack = false;
        while (true)
        {
            if (r.Bit() == 1) { output.Add(r.Byte()); haveDataBack = true; }
            else
            {
                int savedLength = r.UnpackLen(1);
                int savedPos;
                if (haveDataBack && r.Bit() == 1) savedPos = dictPosBack;
                else
                {
                    savedPos = r.DictPos();
                    if (savedPos > 2687) savedLength++;
                }
                haveDataBack = false;
                if (savedPos != 0)
                {
                    int length = savedLength + 2;
                    if (savedPos > output.Count || savedPos > dictSize) throw new InvalidDataException("Invalid TinyUZ match.");
                    if (output.Count + length > maxOutput) throw new InvalidDataException("TinyUZ output exceeds limit.");
                    dictPosBack = savedPos;
                    for (int i = 0; i < length; i++) output.Add(output[^savedPos]);
                }
                else if (savedLength == 1)
                {
                    int length = r.UnpackLen(2) + MinLiteralLine;
                    if (output.Count + length > maxOutput) throw new InvalidDataException("Truncated TinyUZ literal.");
                    output.AddRange(r.Take(length));
                    haveDataBack = true;
                }
                else
                {
                    dictPosBack = 1;
                    r.ResetTypes();
                    if (savedLength == 2) continue;
                    if (savedLength == 3) return [.. output];
                    throw new InvalidDataException("Unknown TinyUZ control code.");
                }
            }
            if (output.Count > maxOutput) throw new InvalidDataException("TinyUZ output exceeds limit.");
        }
    }

    private sealed class Reader(byte[] code)
    {
        private int _pos = 4, _typeCount;
        private byte _types;

        public byte Byte() => _pos < code.Length ? code[_pos++] : throw new InvalidDataException("Truncated TinyUZ stream.");

        public int Bit()
        {
            if (_typeCount == 0) { _types = Byte(); _typeCount = 8; }
            int v = _types & 1;
            _types >>= 1;
            _typeCount--;
            return v;
        }

        public int UnpackLen(int packBits)
        {
            int value = 0;
            while (true)
            {
                int low = 0;
                for (int i = 0; i < packBits; i++) low |= Bit() << i;
                int cont = Bit();
                value = (value << packBits) + low;
                if (cont == 0) return value;
                value++;
            }
        }

        public int DictPos()
        {
            int v = Byte();
            if (v >= 128) v = ((v & 0x7F) | (UnpackLen(2) << 7)) + 128;
            return v;
        }

        public byte[] Take(int n)
        {
            if (_pos + n > code.Length) throw new InvalidDataException("Truncated TinyUZ literal.");
            var s = code[_pos..(_pos + n)];
            _pos += n;
            return s;
        }

        public void ResetTypes() => _typeCount = 0;
    }
}
