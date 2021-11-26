//
// QOKImage is ported version of QOI for C#
// 

/* ---- About original QOI ---- 

QOI - The “Quite OK Image” format for fast, lossless image compression

Dominic Szablewski - https://phoboslab.org


-- LICENSE: The MIT License(MIT)

Copyright(c) 2021 Dominic Szablewski

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files(the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and / or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions :
The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.
THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Buffers.Binary.BinaryPrimitives;

public static class QOKImage
{
    const int QOI_INDEX = 0x0000_0000;
    const int QOI_RUN_8 = 0b0100_0000;
    const int QOI_RUN_16 = 0b0110_0000;
    const int QOI_DIFF_8 = 0b1000_0000;
    const int QOI_DIFF_16 = 0b1100_0000;
    const int QOI_DIFF_24 = 0b1110_0000;
    const int QOI_COLOR = 0b1111_0000;
    const int QOI_MASK_2 = 0b1100_0000;
    const int QOI_MASK_3 = 0b1110_0000;
    const int QOI_MASK_4 = 0b1111_0000;
    const uint QOI_MAGIC = ((uint)'q') << 24 | ((uint)'o') << 16 | ((uint)'i') << 8 | ((uint)'f') << 0;
    const int QOI_HEADER_SIZE = 12;

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

    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, out int out_len)
    {
        if (
            width <= 0 || width > ushort.MaxValue ||
            height <= 0 || height > ushort.MaxValue ||
            channels < 3 || channels > 4 ||
            pixels.Length < width * height * channels
        )
        {
            throw new ArgumentException();
        }

        int max_size = width * height * (channels + 1) + QOI_HEADER_SIZE + QOI_PADDING;
        byte[] bytes = new byte[max_size];

        Span<byte> header_pos = bytes;
        WriteUInt32BigEndian(header_pos, QOI_MAGIC); // "qoif"
        header_pos = header_pos.Slice(sizeof(uint));
        WriteUInt16BigEndian(header_pos, (ushort)width);
        header_pos = header_pos.Slice(sizeof(ushort));
        WriteUInt16BigEndian(header_pos, (ushort)height);
        header_pos = header_pos.Slice(sizeof(ushort));
        WriteUInt32BigEndian(header_pos, 0); // size, will be set later

        int p = EncodeChunk(pixels, channels, bytes.AsSpan(QOI_HEADER_SIZE));

        WriteUInt32BigEndian(header_pos, (uint)p);
        out_len = QOI_HEADER_SIZE + p;
        return bytes;
    }

    private static int EncodeChunk(ReadOnlySpan<byte> pixels, int channels, Span<byte> bytes)
    {
        Span<qoi_rgba_t> index = stackalloc qoi_rgba_t[64];

        int p = 0;
        int run = 0;
        qoi_rgba_t px_prev = new() { rgba = { r = 0, g = 0, b = 0, a = Byte.MaxValue } };
        qoi_rgba_t px = px_prev;

        int px_end = pixels.Length - channels;
        for (int px_pos = 0; px_pos < pixels.Length; px_pos += channels)
        {
            if (channels == 4)
            {
                px = MemoryMarshal.Read<qoi_rgba_t>(pixels.Slice(px_pos));
            }
            else
            {
                Unsafe.CopyBlock(ref px.rgba.r, ref Unsafe.AsRef(in pixels[px_pos]), 3);
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
        return p;
    }

    public static byte[] Decode(ReadOnlySpan<byte> data, out int width, out int height, int channels)
    {
        if (channels < 3 || channels > 4 || data.Length < QOI_HEADER_SIZE)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        var magic = ReadUInt32BigEndian(data.Slice(0));
        width = ReadUInt16BigEndian(data.Slice(4));
        height = ReadUInt16BigEndian(data.Slice(6));
        var size = ReadUInt32BigEndian(data.Slice(8));

        if (
            width == 0 || height == 0 ||
            magic != QOI_MAGIC ||
            (int)size + QOI_HEADER_SIZE != data.Length
        )
        {
            throw new ArgumentOutOfRangeException();
        }

        int px_len = width * height * channels;

        ReadOnlySpan<byte> bytes = data.Slice(QOI_HEADER_SIZE);
        return DecodeChunk(bytes, px_len, channels);
    }

    private static byte[] DecodeChunk(ReadOnlySpan<byte> bytes, int px_len, int channels)
    {
        byte[] pixels = new byte[px_len];
        qoi_rgba_t px = new() { rgba = { r = 0, g = 0, b = 0, a = Byte.MaxValue } };
        Span<qoi_rgba_t> index = stackalloc qoi_rgba_t[64];
        int chunks_len = bytes.Length - QOI_PADDING;

        int run = 0;
        for (int px_pos = 0, p = 0; px_pos < pixels.Length; px_pos += channels)
        {
            if (run > 0)
            {
                run--;
            }
            else if (p < chunks_len)
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
                Unsafe.WriteUnaligned(ref pixels[px_pos], px);
            }
            else
            {
                Unsafe.CopyBlockUnaligned(ref pixels[px_pos], ref px.rgba.r, 3);
            }
        }
        return pixels;
    }
}
