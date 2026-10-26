// Copyright 2026 Ari Sulistiono
// SPDX-License-Identifier: Apache-2.0

namespace ArIEC103.Master.Protocol;

public static class Ft12FrameBuilder
{
    public static byte BuildPrimaryControl(int functionCode, bool fcv = false, bool fcb = false)
    {
        if (functionCode < 0 || functionCode > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(functionCode), "IEC FT1.2 primary function code must be 0..15.");
        }

        var value = 0x40 | (functionCode & 0x0F);
        if (fcb) value |= 0x20;
        if (fcv) value |= 0x10;
        return (byte)value;
    }

    public static byte[] Fixed(byte control, byte linkAddress)
    {
        var checksum = (byte)((control + linkAddress) & 0xFF);
        return new[] { (byte)0x10, control, linkAddress, checksum, (byte)0x16 };
    }

    public static byte[] Variable(byte control, byte linkAddress, IReadOnlyList<byte> asdu)
    {
        asdu ??= Array.Empty<byte>();
        var length = checked((byte)(2 + asdu.Count));
        var frame = new byte[4 + length + 2];
        frame[0] = 0x68;
        frame[1] = length;
        frame[2] = length;
        frame[3] = 0x68;
        frame[4] = control;
        frame[5] = linkAddress;

        for (var i = 0; i < asdu.Count; i++)
        {
            frame[6 + i] = asdu[i];
        }

        var sum = 0;
        for (var i = 4; i < 4 + length; i++)
        {
            sum += frame[i];
        }

        frame[4 + length] = (byte)(sum & 0xFF);
        frame[5 + length] = 0x16;
        return frame;
    }
}
