using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.MemoryMarshal;

public static class QOKImage
{
    const int QOI_INDEX = 0x00000000;
    const int QOI_RUN_8 = 0b01000000;
    const int QOI_RUN_16 = 0b01100000;
    const int QOI_DIFF_8 = 0b10000000;
    const int QOI_DIFF_16 = 0b11000000;
    const int QOI_DIFF_24 = 0b11100000;
    const int QOI_COLOR = 0b11110000;
    const int QOI_MASK_2 = 0b11000000;
    const int QOI_MASK_3 = 0b11100000;
    const int QOI_MASK_4 = 0b11110000;

    const int QOI_PADDING = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct qoi_rgba_t
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct Rgba
        {
            public byte r, g, b, a;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly int GetColorHash() => r ^ g ^ b ^ a;
        }
        public Rgba rgba;

        public readonly uint v
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (uint)rgba.r << 24 | (uint)rgba.g << 16 | (uint)rgba.b << 8 | rgba.a;
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 4)]
    struct qoi_magic_t
    {
        public byte chars0, chars1, chars2, chars3;
        //unsigned int v;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct qoi_header_t
    {
        public qoi_magic_t magic;
        public ushort width;
        public ushort height;
        public uint size;
    };
    static readonly int HeaderSize = Unsafe.SizeOf<qoi_header_t>();

    static ReadOnlySpan<byte> MagicBytes() => new byte[] { (byte)'q', (byte)'o', (byte)'i', (byte)'f' };
    static ref readonly qoi_magic_t Magic() => ref AsRef<qoi_magic_t>(MagicBytes());

    public static byte[] Encode(ReadOnlySpan<byte> pixels, int w, int h, int channels, out int out_len)
    {
        if (
            w <= 0 || w > ushort.MaxValue ||
            h <= 0 || h > ushort.MaxValue ||
            channels < 3 || channels > 4
        )
        {
            throw new ArgumentException();
        }

        int max_size = w * h * (channels + 1) + HeaderSize + QOI_PADDING;
        int p = 0;
        byte[] bytes = new byte[max_size];
        var header = new qoi_header_t
        {
            magic = Magic(),
            width = (ushort)w,
            height = (ushort)h,
            size = 0 // will be set at the end
        };
        Write(bytes, ref header);
        p += HeaderSize;

        Span<qoi_rgba_t> index = stackalloc qoi_rgba_t[64];

        int run = 0;
        qoi_rgba_t px_prev = new() { rgba = { r = 0, g = 0, b = 0, a = Byte.MaxValue } };
        qoi_rgba_t px = px_prev;

        int px_len = w * h * channels;
        int px_end = px_len - channels;
        for (int px_pos = 0; px_pos < px_len; px_pos += channels)
        {
            if (channels == 4)
            {
                px = Read<qoi_rgba_t>(pixels.Slice(px_pos));
            }
            else
            {
                px.rgba.r = pixels[px_pos + 0];
                px.rgba.g = pixels[px_pos + 1];
                px.rgba.b = pixels[px_pos + 2];
            }

            if (px.v == px_prev.v)
            {
                run++;
            }

            if (run > 0 && (run == 0x2020 || px.v != px_prev.v || px_pos == px_end))
            {
                if (run < 33)
                {
                    run -= 1;
                    bytes[p++] = (byte)(QOI_RUN_8 | run);
                }
                else
                {
                    run -= 33;
                    bytes[p++] = (byte)(QOI_RUN_16 | run >> 8);
                    bytes[p++] = (byte)run;
                }
                run = 0;
            }

            if (px.v != px_prev.v)
            {
                int index_pos = px.rgba.GetColorHash() % 64;

                if (index[index_pos].v == px.v)
                {
                    bytes[p++] = (byte)(QOI_INDEX | index_pos);
                }
                else
                {
                    index[index_pos] = px;

                    int vr = px.rgba.r - px_prev.rgba.r;
                    int vg = px.rgba.g - px_prev.rgba.g;
                    int vb = px.rgba.b - px_prev.rgba.b;
                    int va = px.rgba.a - px_prev.rgba.a;

                    if (
                        vr > -16 && vr < 17 && vg > -16 && vg < 17 &&
                        vb > -16 && vb < 17 && va > -16 && va < 17
                    )
                    {
                        if (
                            va == 0 && vr > -2 && vr < 3 &&
                            vg > -2 && vg < 3 && vb > -2 && vb < 3
                        )
                        {
                            bytes[p++] = (byte)(QOI_DIFF_8 | ((vr + 1) << 4) | (vg + 1) << 2 | (vb + 1));
                        }
                        else if (
                            va == 0 && vr > -16 && vr < 17 &&
                            vg > -8 && vg < 9 && vb > -8 && vb < 9
                        )
                        {
                            bytes[p++] = (byte)(QOI_DIFF_16 | (vr + 15));
                            bytes[p++] = (byte)(((vg + 7) << 4) | (vb + 7));
                        }
                        else
                        {
                            bytes[p++] = (byte)(QOI_DIFF_24 | ((vr + 15) >> 1));
                            bytes[p++] = (byte)(((vr + 15) << 7) | ((vg + 15) << 2) | ((vb + 15) >> 3));
                            bytes[p++] = (byte)(((vb + 15) << 5) | (va + 15));
                        }
                    }
                    else
                    {
                        bytes[p++] = (byte)(QOI_COLOR | (vr != 0 ? 8 : 0) | (vg != 0 ? 4 : 0) | (vb != 0 ? 2 : 0) | (va != 0 ? 1 : 0));
                        if (vr != 0) { bytes[p++] = px.rgba.r; }
                        if (vg != 0) { bytes[p++] = px.rgba.g; }
                        if (vb != 0) { bytes[p++] = px.rgba.b; }
                        if (va != 0) { bytes[p++] = px.rgba.a; }
                    }
                }
            }
            px_prev = px;
        }

        p += QOI_PADDING;

        AsRef<qoi_header_t>(bytes).size = (uint)(p - HeaderSize);
        out_len = p;
        return bytes;
    }

    static ReadOnlySpan<byte> AsBytes<T>(in T value) where T : unmanaged
        => MemoryMarshal.AsBytes(CreateReadOnlySpan(ref Unsafe.AsRef(value), 1));

    public static byte[] Decode(ReadOnlySpan<byte> data, out int width, out int height, int channels)
    {
        if (data.Length < HeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        ref readonly qoi_header_t header = ref AsRef<qoi_header_t>(data);
        if (
            channels < 3 || channels > 4 ||
            header.width == 0 || header.height == 0 ||
            (int)header.size + HeaderSize != data.Length ||
            !AsBytes(in header.magic).SequenceEqual(MagicBytes())
        )
        {
            throw new ArgumentOutOfRangeException();
        }

        int px_len = header.width * header.height * channels;
        byte[] pixels = new byte[px_len];

        ReadOnlySpan<byte> bytes = data.Slice(HeaderSize);

        uint data_len = header.size - QOI_PADDING;
        qoi_rgba_t px = new() { rgba = { r = 0, g = 0, b = 0, a = Byte.MaxValue } };
        Span<qoi_rgba_t> index = stackalloc qoi_rgba_t[64];

        int run = 0;
        for (int px_pos = 0, p = 0; px_pos < px_len; px_pos += channels)
        {
            if (run > 0)
            {
                run--;
            }
            else if (p < data_len)
            {
                int b1 = bytes[p++];

                if ((b1 & QOI_MASK_2) == QOI_INDEX)
                {
                    px = index[b1 ^ QOI_INDEX];
                }
                else if ((b1 & QOI_MASK_3) == QOI_RUN_8)
                {
                    run = b1 & 0x1f;
                }
                else if ((b1 & QOI_MASK_3) == QOI_RUN_16)
                {
                    int b2 = bytes[p++];
                    run = (((b1 & 0x1f) << 8) | (b2)) + 32;
                }
                else if ((b1 & QOI_MASK_2) == QOI_DIFF_8)
                {
                    px.rgba.r += (byte)(((b1 >> 4) & 0x03) - 1);
                    px.rgba.g += (byte)(((b1 >> 2) & 0x03) - 1);
                    px.rgba.b += (byte)((b1 & 0x03) - 1);
                }
                else if ((b1 & QOI_MASK_3) == QOI_DIFF_16)
                {
                    int b2 = bytes[p++];
                    px.rgba.r += (byte)((b1 & 0x1f) - 15);
                    px.rgba.g += (byte)((b2 >> 4) - 7);
                    px.rgba.b += (byte)((b2 & 0x0f) - 7);
                }
                else if ((b1 & QOI_MASK_4) == QOI_DIFF_24)
                {
                    int b2 = bytes[p++];
                    int b3 = bytes[p++];
                    px.rgba.r += (byte)((((b1 & 0x0f) << 1) | (b2 >> 7)) - 15);
                    px.rgba.g += (byte)(((b2 & 0x7c) >> 2) - 15);
                    px.rgba.b += (byte)((((b2 & 0x03) << 3) | ((b3 & 0xe0) >> 5)) - 15);
                    px.rgba.a += (byte)((b3 & 0x1f) - 15);
                }
                else if ((b1 & QOI_MASK_4) == QOI_COLOR)
                {
                    if ((b1 & 8) != 0) { px.rgba.r = bytes[p++]; }
                    if ((b1 & 4) != 0) { px.rgba.g = bytes[p++]; }
                    if ((b1 & 2) != 0) { px.rgba.b = bytes[p++]; }
                    if ((b1 & 1) != 0) { px.rgba.a = bytes[p++]; }
                }

                index[px.rgba.GetColorHash() % 64] = px;
            }

            if (channels == 4)
            {
                Write(pixels.AsSpan(px_pos), ref px);
            }
            else
            {
                pixels[px_pos + 0] = px.rgba.r;
                pixels[px_pos + 1] = px.rgba.g;
                pixels[px_pos + 2] = px.rgba.b;
            }
        }

        width = header.width;
        height = header.height;
        return pixels;
    }
}
