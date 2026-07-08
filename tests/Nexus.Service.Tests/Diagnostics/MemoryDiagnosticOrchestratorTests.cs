using Nexus.Service.Diagnostics.Memory;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class MemoryDiagnosticOrchestratorTests
{
    [Fact]
    public void ParseBootSequence_DetectsMemdiagEntry()
    {
        const string output = """
            Windows Boot Manager
            --------------------
            identifier              {bootmgr}
            device                  partition=\Device\HarddiskVolume2
            description             Windows Boot Manager
            locale                  en-US
            inherit                 {globalsettings}
            default                 {current}
            resumeobject            {c1f4f96b-1111-2222-3333-abcdefabcdef}
            displayorder            {current}
            bootsequence            {memdiag}
            timeout                 30
            """;

        Assert.True(MemoryDiagnosticOrchestrator.ParseBootSequenceHasMemdiag(output));
    }

    [Fact]
    public void ParseBootSequence_IsCaseInsensitive()
    {
        const string output = "BOOTSEQUENCE            {MEMDIAG}";
        Assert.True(MemoryDiagnosticOrchestrator.ParseBootSequenceHasMemdiag(output));
    }

    [Fact]
    public void ParseBootSequence_AbsentWhenNoLine()
    {
        const string output = """
            Windows Boot Manager
            --------------------
            identifier              {bootmgr}
            displayorder            {current}
            timeout                 30
            """;

        Assert.False(MemoryDiagnosticOrchestrator.ParseBootSequenceHasMemdiag(output));
    }

    [Fact]
    public void ParseLastResultXml_PassedEvent()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-MemoryDiagnostics-Results"/>
                <EventID>1201</EventID>
                <TimeCreated SystemTime="2026-07-06T12:00:00.0000000Z"/>
              </System>
              <EventData>
                <Data Name="ToolVersion">10.0</Data>
                <Data Name="ErrorCount">0</Data>
              </EventData>
            </Event>
            """;

        var result = MemoryDiagnosticOrchestrator.ParseLastResultXml(xml);

        Assert.NotNull(result);
        Assert.Equal(MemoryTestResult.Passed, result!.Result);
        Assert.Equal(new System.DateTime(2026, 7, 6, 12, 0, 0, System.DateTimeKind.Utc), result.TimeUtc);
    }

    [Fact]
    public void ParseLastResultXml_FailedEvent_PairedWith1202()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-MemoryDiagnostics-Results"/>
                <EventID>1201</EventID>
                <TimeCreated SystemTime="2026-07-06T12:00:00.0000000Z"/>
              </System>
              <EventData>
                <Data Name="ErrorCount">3</Data>
              </EventData>
            </Event>
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-MemoryDiagnostics-Results"/>
                <EventID>1202</EventID>
                <TimeCreated SystemTime="2026-07-06T12:00:05.0000000Z"/>
              </System>
              <EventData>
                <Data Name="BadMemoryModule">DIMM_A1</Data>
              </EventData>
            </Event>
            """;

        var result = MemoryDiagnosticOrchestrator.ParseLastResultXml(xml);

        Assert.NotNull(result);
        Assert.Equal(MemoryTestResult.Failed, result!.Result);
        Assert.Contains("BadMemoryModule", result.Detail!);
    }

    [Fact]
    public void ParseLastResultXml_NoMatchingEvents_ReturnsNull()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-MemoryDiagnostics-Results"/>
                <EventID>9999</EventID>
                <TimeCreated SystemTime="2026-07-06T12:00:00.0000000Z"/>
              </System>
            </Event>
            """;

        Assert.Null(MemoryDiagnosticOrchestrator.ParseLastResultXml(xml));
    }

    [Fact]
    public void ParseLastResultXml_EmptyString_ReturnsNull()
    {
        Assert.Null(MemoryDiagnosticOrchestrator.ParseLastResultXml(""));
    }

    [Fact]
    public void ParseLastResultXml_MalformedXml_ReturnsNullNotThrow()
    {
        Assert.Null(MemoryDiagnosticOrchestrator.ParseLastResultXml("<Event><Unclosed>"));
    }
}
