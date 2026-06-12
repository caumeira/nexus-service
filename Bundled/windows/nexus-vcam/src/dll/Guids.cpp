// Single TU that materializes GUID definitions. initguid.h must precede the
// SDK headers here so KS pin categories and camera profile GUIDs that have no
// import-library home get defined exactly once in this binary.

#include <initguid.h>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mfvirtualcamera.h>
#include <ks.h>
#include <ksmedia.h>

#include "Guids.h"

extern "C" const GUID CLSID_NexusVCam =
    { 0x85867876, 0x6949, 0x4489, { 0xb9, 0xda, 0x7d, 0x71, 0x9f, 0x81, 0xb5, 0x0f } };
