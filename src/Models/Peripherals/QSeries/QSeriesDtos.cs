namespace Nexus.Service.Models.Peripherals.QSeries;

public class QSeriesRotationParams : ApiResponse
{
    /// <summary>One of: Portrait, PortraitFlipped. Null on a POST body leaves
    /// the stored orientation unchanged; GET always returns the stored value.</summary>
    public string? Orientation { get; set; }
}
