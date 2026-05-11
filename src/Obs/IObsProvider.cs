using Qos.Service.Models;
using Qos.Service.Models.Obs;

namespace Qos.Service.Obs;

public interface IObsProvider
{
    ObsConfigResponse GetConfig();
    void SetConfig(ObsConfigBody body);
    Task<ObsStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<ObsStatusResponse> ToggleRecordingAsync(CancellationToken cancellationToken);
    Task<ObsStatusResponse> ToggleStreamingAsync(CancellationToken cancellationToken);
    Task<ObsStatusResponse> SetSceneAsync(string sceneName, CancellationToken cancellationToken);
    ApiResponse Launch();
}
