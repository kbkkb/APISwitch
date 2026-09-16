using System.Text;

namespace APISwitch.Services;

public static class MiniProto
{
    public record Field(int Number, int WireType, ulong Varint, byte[] Bytes);

    public static List<Field> Parse(ReadOnlySpan<byte> data)
    {
        var fields = new List<Field>();
        int i = 0;
        while (i < data.Length)
        {
            var (tag, ni) = ReadVarint(data, i);
            i = ni;
            int number = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (number == 0) break;
            switch (wire)
            {
                case 0:
                    var (v, nv) = ReadVarint(data, i);
                    i = nv;
                    fields.Add(new Field(number, wire, v, Array.Empty<byte>()));
                    break;
                case 1:
                    fields.Add(new Field(number, wire, 0, data.Slice(i, 8).ToArray()));
                    i += 8;
                    break;
                case 2:
                    var (len, nl) = ReadVarint(data, i);
                    i = nl;
                    if (i + (int)len > data.Length) return fields;
                    fields.Add(new Field(number, wire, 0, data.Slice(i, (int)len).ToArray()));
                    i += (int)len;
                    break;
                case 5:
                    fields.Add(new Field(number, wire, 0, data.Slice(i, 4).ToArray()));
                    i += 4;
                    break;
                default:
                    return fields;
            }
        }
        return fields;
    }

    static (ulong Value, int Next) ReadVarint(ReadOnlySpan<byte> data, int i)
    {
        ulong result = 0;
        int shift = 0;
        while (i < data.Length)
        {
            byte b = data[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) break;
        }
        return (result, i);
    }

    public static string? AsString(List<Field> fields, int number)
    {
        var f = fields.FirstOrDefault(x => x.Number == number && x.WireType == 2);
        return f == null ? null : Encoding.UTF8.GetString(f.Bytes);
    }

    public static byte[]? AsBytes(List<Field> fields, int number)
    {
        var f = fields.FirstOrDefault(x => x.Number == number && x.WireType == 2);
        return f?.Bytes;
    }
}
