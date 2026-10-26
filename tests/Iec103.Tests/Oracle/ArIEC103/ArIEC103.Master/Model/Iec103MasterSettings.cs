// Copyright 2026 Ari Sulistiono
// SPDX-License-Identifier: Apache-2.0

using System.IO.Ports;

namespace ArIEC103.Master.Model;

/// <summary>
/// Single-connection IEC-103 master configuration.
/// The product direction is intentionally master-to-relay first:
/// one COM connection to one protection relay acting as IEC-103 slave.
/// </summary>
public sealed class Iec103MasterSettings
{
    public string PortName { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.Even;
    public StopBits StopBits { get; set; } = StopBits.One;

    public byte LinkAddress { get; set; } = 1;
    public byte CommonAddress { get; set; } = 1;

    public bool UseSimulatedSlave { get; set; }
    public string TargetProfile { get; set; } = "IEC-103 protection relay";
    public string MappingProfilePath { get; set; } = string.Empty;

    public int ResponseTimeoutMs { get; set; } = 1500;
    public int Class2PollIntervalMs { get; set; } = 500;
    public int Class1DrainDelayMs { get; set; } = 20;
    public int BusyBackoffMs { get; set; } = 250;
    public int StartupDelayMs { get; set; } = 300;
    public int MaxClass1DrainFrames { get; set; } = 64;
    public int MaxConsecutiveTimeoutsBeforeResetFcb { get; set; } = 3;
    public int TimeoutRecoveryBackoffMs { get; set; } = 250;

    public bool ResetRemoteLinkOnConnect { get; set; } = false;
    public bool ResetFcbOnConnect { get; set; } = true;
    public bool SendGeneralInterrogationOnConnect { get; set; } = true;
    public bool SendClockSyncOnConnect { get; set; } = false;
    public bool RequestClass2ImmediatelyAfterStartup { get; set; } = true;
    public bool ResetFcbAfterTimeoutBurst { get; set; } = true;

    /// <summary>
    /// Public reports should not expose local folder/customer paths by default.
    /// Enable this only for private debugging where the full workstation path is useful.
    /// </summary>
    public bool IncludeLocalPathsInReports { get; set; } = false;

    /// <summary>
    /// Returns a copy suitable for Markdown/JSON evidence export.
    /// Operational settings are preserved, while local path fields are reduced to file names
    /// unless IncludeLocalPathsInReports is explicitly enabled.
    /// </summary>
    public Iec103MasterSettings CreateReportSnapshot()
    {
        var copy = (Iec103MasterSettings)MemberwiseClone();
        if (!IncludeLocalPathsInReports && !string.IsNullOrWhiteSpace(copy.MappingProfilePath))
        {
            copy.MappingProfilePath = Path.GetFileName(copy.MappingProfilePath);
        }

        return copy;
    }

    /// <summary>
    /// Memory guard for long polling sessions. Counters always keep full totals, but retained
    /// evidence is bounded so the desktop app and JSON export stay responsive.
    /// </summary>
    public int MaxRetainedEvidenceEvents { get; set; } = 10000;
    public int MaxRetainedRelayEvents { get; set; } = 5000;
    public int MaxRetainedFindings { get; set; } = 1000;

    public static Iec103MasterSettings CreateDefault() => new();

    public string SerialSummary => UseSimulatedSlave
        ? $"Simulated {TargetProfile}, Link={LinkAddress}, CA={CommonAddress}"
        : $"{PortName}, {BaudRate} bps, {DataBits}{ParityText}{StopBitsText}, Link={LinkAddress}, CA={CommonAddress}";

    private string ParityText => Parity switch
    {
        Parity.None => "N",
        Parity.Odd => "O",
        Parity.Even => "E",
        Parity.Mark => "M",
        Parity.Space => "S",
        _ => Parity.ToString()
    };

    private string StopBitsText => StopBits switch
    {
        StopBits.One => "1",
        StopBits.Two => "2",
        StopBits.OnePointFive => "1.5",
        _ => StopBits.ToString()
    };
}
