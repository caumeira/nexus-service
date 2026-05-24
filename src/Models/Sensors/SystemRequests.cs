using System.Collections.Generic;

namespace Nexus.Service.Models.Sensors;

public class SetPollingRateBody
{
    public int PollingRate { get; set; }
}

public class GetPollingRateResponse
{
    public int PollingRate { get; set; }
}

public class ProcessElevationResponse
{
    public string Platform { get; set; } = "";
    public bool Supported { get; set; }
    public bool IsElevated { get; set; }
    public string Status { get; set; } = "";
}

public class ProcessElevationRelaunchResponse
{
    public string Result { get; set; } = "";
}

public class GetModelResponse
{
    public string Model { get; set; } = "";
}

public class GetGpuModelsResponse : ApiResponse
{
    public List<string> Models { get; set; } = new();
}

public class CpuHealthResponse : ApiResponse
{
    public bool Healthy { get; set; } = true;
    public float DistanceToTJMax { get; set; }
}

public class GetStoragePartitionsResponse : ApiResponse
{
    public List<string> Partitions { get; set; } = new();
}

public class GetDriveStorageResponse : ApiResponse
{
    public List<StorageDriveInfo> Storage { get; set; } = new();
}
