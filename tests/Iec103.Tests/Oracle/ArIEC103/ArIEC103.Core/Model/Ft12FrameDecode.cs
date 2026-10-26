// Copyright 2026 Ari Sulistiono
// SPDX-License-Identifier: Apache-2.0

namespace ArIEC103.Core.Model;

public sealed class Ft12FrameDecode
{
    public Ft12FrameFormat Format { get; init; }
    public IReadOnlyList<byte> RawBytes { get; init; } = Array.Empty<byte>();
    public byte? Control { get; init; }
    public byte? LinkAddress { get; init; }
    public byte? Checksum { get; init; }
    public byte? CalculatedChecksum { get; init; }
    public bool IsChecksumValid { get; init; }
    public bool IsLengthValid { get; init; }
    public int? DeclaredLength { get; init; }
    public LinkControlInfo? LinkControl { get; init; }
    public AsduDecode? Asdu { get; init; }
    public IReadOnlyList<byte> AsduBytes { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();

    public string Hex => string.Join(" ", RawBytes.Select(x => x.ToString("X2")));

    public string ShortMeaning
    {
        get
        {
            if (Format == Ft12FrameFormat.SingleCharacter) return "Single character ACK";
            if (LinkControl is null) return "Malformed or unsupported frame";
            if (Asdu is not null)
            {
                var semantic = string.IsNullOrWhiteSpace(Asdu.SemanticLabel) ? string.Empty : " - " + Asdu.SemanticLabel;
                return $"{LinkControl.FunctionName}; {Asdu.TypeName}{semantic}";
            }
            return LinkControl.FunctionName;
        }
    }
}
