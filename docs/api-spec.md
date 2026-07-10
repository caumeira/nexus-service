# Nexus Service - API Spec

Source of truth for the contract test suite. Mirrors the the original control service surface 1:1 by route + verb.
Each endpoint has an `impl` column: **real** = working implementation, **stub** = returns well-formed empty/disconnected data, **macOS** = real on macOS only.

## /ping
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET | `/ping` | real | `{ service, version, initialized, isReady }` |

## /system
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/system/snapshot`        | macOS | `{ cpu, memory, gpu, source }` cross-platform abstraction |
| GET  | `/system/os-version`      | real  | `ApiResponse { msg }` with OS string |
| POST | `/system/polling-rate`    | stub  | accept `{ pollingRate }` |
| GET  | `/system/cpu/sensors`     | stub  | `ISystemSensor[]` empty |
| GET  | `/system/cpu/model`       | macOS | `{ model }` |
| GET  | `/system/cpu/health`      | stub  | `{ healthy, distanceToTJMax }` |
| WS   | `/system/cpu`             | stub  | streams `HardwareComponent` |
| GET  | `/system/gpu/sensors`     | stub  | |
| GET  | `/system/gpu/model`       | macOS | `{ models[] }` |
| WS   | `/system/gpu`             | stub  | |
| GET  | `/system/memory/sensors`  | stub  | |
| GET  | `/system/memory/total`    | macOS | `ApiResponse { msg }` |
| WS   | `/system/memory`          | stub  | |
| GET  | `/system/storage/sensors` | stub  | |
| GET  | `/system/storage/partitions` | stub | `{ partitions[] }` |
| GET  | `/system/storage/info`    | stub  | `{ storage[] }` |
| WS   | `/system/storage`         | stub  | |
| GET  | `/system/motherboard/sensors` | stub | |
| GET  | `/system/motherboard/model`   | stub | |
| WS   | `/system/motherboard`     | stub  | |
| GET  | `/system/fps/sensors`     | real on Windows | Cached snapshot only; does not start capture by itself. |
| WS   | `/ws` topic `fps`         | real on Windows | Streams `HardwareComponent`; starts foreground-window ETW capture only while subscribed. |
| WS   | `/system/sensor/{device}` | stub  | query: id, type, name, index, drive |

## /cooling
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/cooling`                  | stub | |
| GET  | `/cooling/q60`              | stub | |
| GET  | `/cooling/q80`              | stub | |
| GET  | `/cooling/minihub`          | stub | |
| GET  | `/cooling/np50`             | stub | |
| POST | `/cooling/layout`           | stub | accept `{ port1..port4 }` |
| GET  | `/cooling/aio/turbo`        | stub | |
| POST | `/cooling/aio/turbo`        | stub | |
| GET  | `/cooling/aio/animations`   | stub | |
| GET  | `/cooling/aio/animation`    | stub | |
| POST | `/cooling/aio/animation`    | stub | |
| GET  | `/cooling/aio/mode`         | stub | |
| POST | `/cooling/aio/mode/{mode}`  | stub | |
| GET  | `/cooling/aio/curve`        | stub | |
| POST | `/cooling/aio/curve`        | stub | |
| GET  | `/cooling/portal/speed`     | stub | |
| POST | `/cooling/portal/speed/{speed}` | stub | |
| GET  | `/cooling/portal/mode`      | stub | |
| POST | `/cooling/portal/mode/{mode}` | stub | |
| GET  | `/cooling/portal/animations` | stub | |
| GET  | `/cooling/portal/animation` | stub | |
| POST | `/cooling/portal/animation` | stub | |
| WS   | `/cooling/curves/calculated` | stub | query: id required |
| POST | `/cooling/curves/set`       | real (persist) | curve config persists via IConfigStore |

## /lighting
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/lighting/output`             | stub | binary frames |
| POST | `/lighting/stop`               | stub | |
| POST | `/lighting/frame-rate`         | stub | |
| POST | `/lighting/scale-ratio`        | stub | |
| GET  | `/lighting/current`            | real (persist) | reads sync mode from IConfigStore |
| POST | `/lighting/brightness`         | real (persist) | |
| POST | `/lighting/speed`              | real (persist) | |
| WS   | `/lighting/static`             | stub | |
| POST | `/lighting/static/headless-start` | stub | |
| WS   | `/lighting/animate`            | stub | |
| POST | `/lighting/animate/headless-start` | stub | |
| WS   | `/lighting/music`              | stub | |
| POST | `/lighting/music/headless-start` | stub | |
| WS   | `/lighting/screen`             | stub | |
| POST | `/lighting/screen/headless-start` | stub | |
| WS   | `/lighting/config`             | stub | |
| WS   | `/lighting/streaming`          | stub | |
| POST | `/lighting/streaming/set-streaming` | stub | |

## /devices
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/devices/{type}/connected`     | stub | always `false` |
| GET  | `/devices/fw/{type}/version`    | stub | |
| GET  | `/devices/cnvs`                 | stub | |
| POST | `/devices/cnvs`                 | stub | |
| POST | `/devices/can-update`           | stub | always `false` |
| POST | `/devices/update`               | stub | |
| POST | `/devices/update-progress`      | stub | |
| GET  | `/devices/motherboard/leds`     | stub | empty channels |
| POST | `/devices/motherboard/leds`     | stub | |
| POST | `/devices/function-check`       | stub | always `false` |
| WS   | `/devices/lighting-devices`     | stub | |
| POST | `/devices/lighting-devices/disable`     | stub | |
| POST | `/devices/lighting-devices/brightness`  | stub | |
| POST | `/devices/lighting-devices/hue`         | stub | |
| POST | `/devices/lighting-devices/saturation`  | stub | |

## /keeb
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/keeb`                          | stub | |
| GET  | `/keeb/settings`                 | stub | |
| GET  | `/keeb/rotary/functions`         | stub | enum names from WheelFunctions |
| POST | `/keeb/rotary`                   | real (persist) | persist rotary assignment |
| POST | `/keeb/rotary/sensitivity`       | real (persist) | |
| POST | `/keeb/key-reactive`             | real (persist) | |
| POST | `/keeb/firmware/lighting`        | real (persist) | |
| POST | `/keeb/game-mode`                | real (persist) | |
| GET  | `/keeb/macro/{index}`            | real (persist) | |
| POST | `/keeb/macro/{index}`            | real (persist) | |
| WS   | `/keeb/tester`                   | stub | |
| POST | `/inputter`                      | stub | Windows-only behavior |

## /y70
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/y70/status`     | stub | `{ isConnected: false }` |
| GET  | `/y70/rotation`   | stub | |
| POST | `/y70/rotation`   | real (persist) | |
| GET  | `/y70/brightness` | stub | |
| POST | `/y70/brightness` | real (persist) | |
| GET  | `/y70/toggle`     | stub | |
| POST | `/y70/toggle`     | real (persist) | |
| GET  | `/y70/is-rotated` | stub | |

## /qseries
Q-series (Q60 + Q80). The two AIO LCD variants behave identically and
share routes; PID-level distinction lives in `QSeriesHandler`.
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/qseries/serial`    | stub | |
| GET  | `/qseries/timev0`    | real | formatted server time |

## /displays
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/displays` | Windows real, other platforms stub | System monitor list with brightness capability, current brightness, and provider write policy. |
| GET  | `/displays/{id}/brightness` | Windows real, other platforms stub | Current brightness as 0-100 percent. |
| POST | `/displays/{id}/brightness` | Windows real, other platforms stub | Accepts `{ brightness }`; service coalesces per-display writes and returns requested/applied/status. |
| GET  | `/displays/{id}/vcp/{code}` | Windows real, other platforms stub | Raw DDC/CI VCP read, bearer-token auth only from non-panel clients. |
| POST | `/displays/{id}/vcp/{code}` | Windows real, other platforms stub | Raw DDC/CI VCP write, bearer-token auth only from non-panel clients. |

## /aw5d
| Verb | Path | impl | Notes |
|---|---|---|---|
| POST | `/aw5d/launch`     | stub | returns `Status: NotRunning` |
| POST | `/aw5d/terminate`  | stub | |
| GET  | `/aw5d/status`     | stub | |

## /screentime
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/screentime`        | macOS | broadcasts FocusSession |
| GET  | `/screentime/image`  | stub  | empty PNG |

## /appdetection
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/appdetection`         | macOS | broadcasts IDetected[] |
| POST | `/appdetection/kill/{id}` | macOS | |

## /media
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/media`                       | macOS | broadcasts session dict |
| GET  | `/media/{source}/album-art`    | macOS | |
| GET  | `/media/library`               | all | lighting media library; omits entries missing metadata, thumbnail, or frame data |
| DELETE | `/media/{id}`                 | all | idempotent for valid IDs; best-effort cleanup of stale or incomplete media folders |

## /shortcuts
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/shortcuts`         | macOS | optional `targetId` |
| GET  | `/shortcuts/icon`    | macOS | requires `targetId` |
| POST | `/shortcuts/launch`  | macOS | requires `targetId` |

## /beats
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/beats` | stub | broadcasts MusicResult |

## /warning
| Verb | Path | impl | Notes |
|---|---|---|---|
| WS   | `/warning` | stub | broadcasts string warnings |

## /pawnio
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/pawnio` | stub | always `false` on non-Windows |

## /start (Windows Task Scheduler)
| Verb | Path | impl | Notes |
|---|---|---|---|
| GET  | `/start` | stub | `{ enabled: false }` on macOS |
| POST | `/start` | stub | accepted, no-op on macOS |

## /shutdown
| Verb | Path | impl | Notes |
|---|---|---|---|
| POST | `/shutdown` | real | calls all provider Dispose, returns ApiResponse |

## Provider interfaces
- `IPerformanceProvider` ✅ exists
- `ISensorProvider` - CPU/GPU/Memory/Storage/Motherboard/Fps/Sensor for the System controllers
- `ICoolingProvider` - cooling components (GetAll)
- `ICurveProvider` - fan curve registration + calculation events
- `ILightingProvider` - sync state, brightness, speed, frame rate
- `IStaticSyncProvider`, `IAnimateSyncProvider`, `IMusicSyncProvider`, `IScreenSyncProvider`, `IGifSyncProvider`, `IStreamingProvider`
- `ILightingDeviceProvider` - device list + per-device hue/sat/brightness
- `IDeviceProvider` - USB enumeration, firmware updates, motherboard LEDs, CNVS
- `IKeebProvider` - keyboard state, settings, macros, rotary, game mode
- `IInputterProvider` - keystroke playback
- `IY70Provider` - display status, rotation, brightness
- `IQ60Provider` - Q60 status, serial, time
- `IScreenTimeProvider` - focus sessions, today usage (Windows/macOS/Linux real)
- `IAppDetectionProvider` - detected RGB apps (macOS real)
- `IMediaProvider` - playback sessions + control (macOS real)
- `IShortcutsProvider` - installed shortcuts + icons (macOS real)
- `IBeatsProvider` - music analysis stream
- `IWarningProvider` - USB warning event stream
- `IPawnIoProvider` - Windows-only stub
- `IStartupProvider` - autostart toggle

## Persistence (IConfigStore)
File: `~/Library/Application Support/Nexus/settings.json` (macOS), `%LOCALAPPDATA%/Nexus/settings.json` (Windows).
Atomic write via tmp+rename. Schema-versioned. Sections:
- `lighting` - current sync, brightness, speed, frame rate, scale ratio
- `keeb` - game mode, rotary functions, sensitivity, macros, firmware lighting
- `cooling` - fan curves, mini-hub layout
- `y70` - rotation, brightness, toggle state
- `devices` - disabled devices, motherboard LEDs, CNVS settings
