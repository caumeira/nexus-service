#pragma once

#include <guiddef.h>

// Media source CLSID registered under HKLM and passed as the sourceId of
// MFCreateVirtualCamera. Keep in sync with NOTES.md and the installer.
// {85867876-6949-4489-B9DA-7D719F81B50F}
extern "C" const GUID CLSID_NexusVCam;

#define NEXUS_VCAM_CLSID_STRING L"{85867876-6949-4489-B9DA-7D719F81B50F}"
#define NEXUS_VCAM_FRIENDLY_NAME L"Nexus Camera"
