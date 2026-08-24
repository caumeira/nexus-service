# WebSocket Topic RBAC - Design Note

**Status:** proposal, not implemented. Tracks the gap identified in the
2026-05-13 security audit.

## Problem

`/ws` is a single multiplexed topic socket. Any client that passes the
bearer / panel cookie auth gate during the upgrade can subsequently send
`{"sub":["<topic>"]}` for any topic the service exposes. There is no
per-topic permission check inside `MultiplexHub`.

That means a paired *phone* session - which we want to restrict to the
panel surface (lighting, presence) - currently has the same topic
surface as the *desktop* dashboard session, including diagnostic streams
that contain process names (CPU and GPU), network connections, and
screen-time data.

## Current topics

Enumerated from `RegisterSnapshotProvider` and `BroadcastTopicAsync`
call sites in `src/Monitoring`, `src/Sensors`, `src/Activity`, and the
audio pipeline.

| Topic         | Source                | Sensitivity | Phone-OK? |
|---------------|-----------------------|-------------|-----------|
| `summary`     | sensor composite      | low         | yes       |
| `cpu`         | sensor composite      | low         | yes       |
| `gpu`         | sensor composite      | low         | yes       |
| `memory`      | sensor composite      | low         | yes       |
| `storage`     | sensor composite      | low         | yes       |
| `motherboard` | sensor composite      | low         | yes       |
| `cooling` / `cooling-realtime` | curve engine | low    | yes       |
| `extras`      | sensor composite      | low         | yes       |
| `monitoring`  | composite of above    | low         | yes       |
| `fps`         | Windows ETW capture   | low         | yes       |
| `volume`      | media session         | low         | yes       |
| `audio`       | spectrum analyser     | low         | yes       |
| **`processes`**     | top-N process list  | **medium - leaks user app usage**    | **no** |
| **`gpu-processes`** | per-PID GPU usage   | **medium - leaks user app usage**    | **no** |
| **`network`**       | per-PID throughput  | **medium - leaks browsing patterns** | **no** |
| **`screentime`**    | per-app daily time  | **medium - privacy-sensitive**       | **no** |
| `twitch/chat/{channel}` | Twitch IRC reader | low - public chat | yes |

The four highlighted topics expose user-activity signal that a phone
client (potentially handed to a guest, propped in the kitchen, or pinned
on the lock screen) should not see. `gpu-processes` names the process
behind each GPU PID, the same leak class as `processes`. The desktop
dashboard, which runs under the same login as the user, can see them all.

## Proposal

Add a per-topic policy in `MultiplexHub.HandleSubscribe(...)`:

```csharp
private static readonly HashSet<string> DesktopOnlyTopics = new(StringComparer.OrdinalIgnoreCase)
{
    "processes",
    "gpu-processes",
    "network",
    "screentime",
};

private bool TopicAllowedFor(ClientSession session, string topic)
    => !DesktopOnlyTopics.Contains(topic) || session.Scope == AuthScope.Desktop;
```

Where `ClientSession.Scope` is set during the WS upgrade based on the
auth path that resolved the request (`AuthRequestPolicy.RequireBearer`
→ Desktop; `AuthRequestPolicy.AllowPanel` cookie / phone session token
→ Panel).

Rejected `sub` frames should be acknowledged with an explicit
`{"err":"forbidden","topic":"..."}` so the client can suppress the UI
that would have driven the subscribe instead of silently spinning.

## Out of scope for this note

- Topic-level **rate** limits (separate hardening item - bound queue
  depth + per-client send budget in `MultiplexHub`).
- Frame size limits (handled by `WebSocketOptions.KeepAliveInterval`
  and an explicit per-client receive buffer cap).
- Killswitch propagation - needs a hub event so the phone-control
  killswitch can close all phone sessions inside one tick instead of
  waiting for natural disconnect.

## When to implement

Bundle with the WS hardening pass that adds frame size / queue depth
limits, so the `MultiplexHub` is only touched once.
