using System.Collections.Generic;
using Nexus.Service.Models;

namespace Nexus.Service.Models.Cloud;

// Local /cloud/... route shapes - what the dashboard sees. Cloud creds/tokens
// never appear here; only account metadata and sync status.

public sealed class CloudAccountSummaryDto
{
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public CloudAvatarDto? Avatar { get; set; }
    public bool IsPrivate { get; set; }
    public bool EmailVerified { get; set; }
    public bool Active { get; set; }
    public string LastSyncAt { get; set; } = "";
}

public sealed class CloudAccountsResponse : ApiResponse
{
    public List<CloudAccountSummaryDto> Accounts { get; set; } = new();
    public string? ActiveAccountId { get; set; }
}

public sealed class CloudRegisterBody
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Username { get; set; } = "";
}

public sealed class CloudLoginBody
{
    public string Identifier { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class CloudLoginResponse : ApiResponse
{
    public CloudAccountSummaryDto? Account { get; set; }
}

public sealed class CloudLogoutBody
{
    public string AccountId { get; set; } = "";
}

public sealed class CloudRecoveryStartBody
{
    public string Email { get; set; } = "";
}

public sealed class CloudRecoveryStatusResponse : ApiResponse
{
    /// <summary>"idle" | "pending" | "approved" | "expired"</summary>
    public string Status { get; set; } = "idle";
    public bool RecoveryFresh { get; set; }
}

public sealed class CloudPasswordBody
{
    public string? CurrentPassword { get; set; }
    public string NewPassword { get; set; } = "";
}

public sealed class CloudUsernameBody
{
    public string Username { get; set; } = "";
}

public sealed class CloudSetPrivateBody
{
    public bool IsPrivate { get; set; }
}

public sealed class CloudDeleteAccountBody
{
    public string? CurrentPassword { get; set; }
}

public sealed class CloudAvatarResponse : ApiResponse
{
    public CloudAvatarDto? Avatar { get; set; }
}

public sealed class CloudSyncConflictDto
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string LocalUpdatedAt { get; set; } = "";
    public int CloudRevision { get; set; }
    public string CloudUpdatedAt { get; set; } = "";
    public string CloudName { get; set; } = "";
    public string UpdatedByInstallId { get; set; } = "";
}

public sealed class CloudSyncStatusResponse : ApiResponse
{
    /// <summary>"idle" | "syncing" | "dirty" | "offline" | "error"</summary>
    public string State { get; set; } = "idle";
    public string LastSyncAt { get; set; } = "";
    public List<CloudSyncConflictDto> Conflicts { get; set; } = new();
}

public sealed class CloudSyncResolveBody
{
    public string ProfileId { get; set; } = "";
    /// <summary>"local" | "cloud"</summary>
    public string Choice { get; set; } = "";
}
