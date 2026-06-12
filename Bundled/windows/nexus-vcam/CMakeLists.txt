# Nexus virtual camera (Windows 11 Media Foundation).
# Builds NexusVCam.dll (media source COM server, loaded by the frame server)
# and NexusVCamHost.exe (registration + lifetime + feed test harness).
# A dshow/ sibling directory will host the Win10 DirectShow softcam later.

cmake_minimum_required(VERSION 3.20)

project(NexusVCam LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "NexusVCam targets Windows x64 with MSVC only")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static CRT: the DLL is loaded into the FrameServer svchost, which must not
# depend on a VC redist being present.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_compile_options(/W4 /permissive- /EHsc /utf-8)
add_compile_definitions(UNICODE _UNICODE)

set(VCAM_DLL_SOURCES
    src/dll/DllMain.cpp
    src/dll/ControlApi.cpp
    src/dll/Activator.cpp
    src/dll/MediaSource.cpp
    src/dll/MediaStream.cpp
    src/dll/FrameSource.cpp
    src/dll/Guids.cpp
    src/dll/NexusVCam.def
)

add_library(NexusVCam SHARED ${VCAM_DLL_SOURCES})
target_link_libraries(NexusVCam PRIVATE
    mfplat
    mf
    mfuuid
    mfsensorgroup
    ole32
    oleaut32
    advapi32
    uuid
)

add_executable(NexusVCamHost src/host/Main.cpp)
target_link_libraries(NexusVCamHost PRIVATE
    mfplat
    mfuuid
    mfsensorgroup
    ole32
    advapi32
    uuid
)
