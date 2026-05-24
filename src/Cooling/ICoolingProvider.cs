using System.Collections.Generic;
using Nexus.Service.Models.Cooling;

namespace Nexus.Service.Cooling;

/// <summary>
/// Read interface for cooling components (AIOs, hubs, fans, pumps). Stub
/// returns an empty list; WindowsFanControlProvider populates from LHM.
/// </summary>
public interface ICoolingProvider
{
    IReadOnlyList<CoolingComponent> GetAll();
}

/// <summary>Fan curve registration. Curves persist to settings.json via IConfigStore.</summary>
public interface ICurveProvider
{
    void SetCurves(SetCurvesBody body);
    object? GetCalculatedById(string id);
}
