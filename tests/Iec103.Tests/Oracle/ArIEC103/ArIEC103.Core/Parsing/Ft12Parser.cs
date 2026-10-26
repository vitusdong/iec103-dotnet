// Copyright 2026 Ari Sulistiono
// SPDX-License-Identifier: Apache-2.0

using ArIEC103.Core.Model;

namespace ArIEC103.Core.Parsing;

public sealed class Ft12Parser
{
    private readonly AsduDecoder _asduDecoder = new();

    public Ft12FrameDecode Decode(IReadOnlyList<byte> bytes)
    {
        var issues = new List<string>();
        if (bytes.Count == 0)
        {
            return Malformed(bytes, "Empty frame.");
        }

        if (bytes.Count == 1 && bytes[0] == 0xE5)
        {
            return new Ft12FrameDecode
            {
                Format = Ft12FrameFormat.SingleCharacter,
                RawBytes = bytes.ToArray(),
                IsLengthValid = true,
                IsChecksumValid = true
            };
        }

        if (bytes[0] == 0x10)
        {
            return DecodeFixed(bytes, issues);
        }

        if (bytes[0] == 0x68)
        {
            return DecodeVariable(bytes, issues);
        }

        return Malformed(bytes, $"Unsupported start byte 0x{bytes[0]:X2}.");
    }

    private Ft12FrameDecode DecodeFixed(IReadOnlyList<byte> bytes, List<string> issues)
    {
        if (bytes.Count != 5)
        {
            issues.Add($"Fixed frame must contain exactly 5 bytes, actual={bytes.Count}.");
            return BuildFrame(Ft12FrameFormat.Malformed, bytes, issues, null, null, null, null, false, false, null, Array.Empty<byte>(), null);
        }

        var endOk = bytes[4] == 0x16;
        if (!endOk) issues.Add($"Invalid fixed frame end byte 0x{bytes[4]:X2}; expected 0x16.");

        var control = bytes[1];
        var address = bytes[2];
        var checksum = bytes[3];
        var calculated = (byte)((control + address) & 0xFF);
        var checksumOk = checksum == calculated;
        if (!checksumOk) issues.Add($"Checksum mismatch. Calculated 0x{calculated:X2}, received 0x{checksum:X2}.");

        var link = LinkControlDecoder.Decode(control);
        return BuildFrame(endOk ? Ft12FrameFormat.FixedLength : Ft12FrameFormat.Malformed, bytes, issues, control, address, checksum, calculated, checksumOk, endOk, link, Array.Empty<byte>(), null);
    }

    private Ft12FrameDecode DecodeVariable(IReadOnlyList<byte> bytes, List<string> issues)
    {
        if (bytes.Count < 6)
        {
            return Malformed(bytes, "Variable frame is too short.");
        }

        var length1 = bytes[1];
        var length2 = bytes[2];
        var repeatedLengthOk = length1 == length2;
        if (!repeatedLengthOk) issues.Add($"Length bytes differ: L1={length1}, L2={length2}.");

        var secondStartOk = bytes[3] == 0x68;
        if (!secondStartOk) issues.Add($"Invalid second start byte 0x{bytes[3]:X2}; expected 0x68.");

        var expectedCount = 4 + length1 + 2;
        var lengthOk = bytes.Count == expectedCount;
        if (!lengthOk) issues.Add($"Variable frame length mismatch. Declared={length1}, expected total={expectedCount}, actual={bytes.Count}.");

        if (bytes.Count < Math.Min(expectedCount, 6))
        {
            return BuildFrame(Ft12FrameFormat.Malformed, bytes, issues, null, null, null, null, false, false, null, Array.Empty<byte>(), null);
        }

        var checksumIndex = Math.Min(4 + length1, bytes.Count - 2);
        var endIndex = checksumIndex + 1;
        var endOk = endIndex < bytes.Count && bytes[endIndex] == 0x16;
        if (!endOk) issues.Add("Invalid or missing variable frame end byte 0x16.");

        var control = bytes.Count > 4 ? bytes[4] : (byte?)null;
        var address = bytes.Count > 5 ? bytes[5] : (byte?)null;
        var checksum = checksumIndex < bytes.Count ? bytes[checksumIndex] : (byte?)null;
        byte? calculated = null;
        var checksumOk = false;

        if (bytes.Count >= 6 && checksum.HasValue && bytes.Count >= 4 + length1)
        {
            var sum = 0;
            for (var i = 4; i < 4 + length1 && i < bytes.Count; i++) sum += bytes[i];
            calculated = (byte)(sum & 0xFF);
            checksumOk = calculated == checksum;
            if (!checksumOk) issues.Add($"Checksum mismatch. Calculated 0x{calculated:X2}, received 0x{checksum:X2}.");
        }

        var asduBytes = Array.Empty<byte>();
        AsduDecode? asdu = null;
        LinkControlInfo? link = null;

        if (control.HasValue)
        {
            link = LinkControlDecoder.Decode(control.Value);
        }

        if (length1 >= 2 && bytes.Count >= 6)
        {
            var asduLength = Math.Max(0, length1 - 2);
            asduBytes = bytes.Skip(6).Take(asduLength).ToArray();
            if (asduBytes.Length > 0)
            {
                asdu = _asduDecoder.Decode(asduBytes);
            }
        }

        var format = repeatedLengthOk && secondStartOk && lengthOk && endOk ? Ft12FrameFormat.VariableLength : Ft12FrameFormat.Malformed;
        return BuildFrame(format, bytes, issues, control, address, checksum, calculated, checksumOk, repeatedLengthOk && secondStartOk && lengthOk && endOk, link, asduBytes, asdu, length1);
    }

    private static Ft12FrameDecode BuildFrame(
        Ft12FrameFormat format,
        IReadOnlyList<byte> raw,
        IReadOnlyList<string> issues,
        byte? control,
        byte? address,
        byte? checksum,
        byte? calculated,
        bool checksumOk,
        bool lengthOk,
        LinkControlInfo? link,
        IReadOnlyList<byte> asduBytes,
        AsduDecode? asdu,
        int? declaredLength = null)
    {
        return new Ft12FrameDecode
        {
            Format = format,
            RawBytes = raw.ToArray(),
            Control = control,
            LinkAddress = address,
            Checksum = checksum,
            CalculatedChecksum = calculated,
            IsChecksumValid = checksumOk,
            IsLengthValid = lengthOk,
            DeclaredLength = declaredLength,
            LinkControl = link,
            AsduBytes = asduBytes.ToArray(),
            Asdu = asdu,
            Issues = issues.ToArray()
        };
    }

    private static Ft12FrameDecode Malformed(IReadOnlyList<byte> bytes, string issue)
    {
        return new Ft12FrameDecode
        {
            Format = Ft12FrameFormat.Malformed,
            RawBytes = bytes.ToArray(),
            IsChecksumValid = false,
            IsLengthValid = false,
            Issues = new[] { issue }
        };
    }
}
