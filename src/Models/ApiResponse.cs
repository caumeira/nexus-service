using System.Text.Json.Serialization;

namespace Nexus.Service.Models;

/// <summary>
/// Base envelope returned by every endpoint that doesn't have a more specific shape.
/// </summary>
public class ApiResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "Ok";

    /// <summary>Diagnostic only — never serialized to clients, never trusted from clients.</summary>
    [JsonIgnore]
    public System.Exception? Exception { get; set; }

    public static ApiResponse Ok(string msg = "Ok") => new() { Error = false, Msg = msg };
    public static ApiResponse Fail(string msg, System.Exception? ex = null) => new()
    {
        Error = true,
        Msg = msg,
        Exception = ex,
    };
}
