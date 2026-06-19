// nexus-overlay-helper
//
// Per-screen transparent borderless NSWindow hosting WKWebView for the
// floating desktop widgets. Companion to nexus-overlay.exe on Windows;
// the service spawns this helper when the user has at least one widget
// pinned, kills it when the last widget is unpinned.
//
// Why a sidecar: the AppKit / WebKit window-creation chain has subtle
// ordering and notification semantics that are painful to drive through
// raw objc_msgSend P/Invoke from Native AOT C#. Native Swift expresses
// this in ~150 lines without the bridge friction.
//
// CLI: nexus-overlay-helper --port=9400 --token=<bearer>
//   --port  : nexus-service HTTP port. Defaults to 9400.
//   --token : auth bearer token from /pair. Required - the WKWebView
//             round-trips it to the service for /overlay routes.
//
// Window contract:
// * Borderless, transparent, click-through (window.ignoresMouseEvents = true).
// * One window per NSScreen; each navigates to /overlay?monitor=N&token=T.
// * Stays on every Space, doesn't appear in cmd-tab.
// * Status-window level when --always-on-top, normal level otherwise.
// * SPA -> host bridge via WKScriptMessageHandler named "nexusOverlay".
//
// Lifecycle: dies on parent process exit (we monitor stdin EOF). Re-spawned
// by the service on demand. Single instance via PID file in
// ~/Library/Application Support/Nexus/.

import AppKit
import WebKit
import Foundation

// MARK: - CLI parsing

struct Options {
    var port: Int = 9400
    var token: String = ""
    var alwaysOnTop: Bool = false
}

func parseOptions() -> Options {
    var o = Options()
    for arg in CommandLine.arguments.dropFirst() {
        // Only accept --key=value form. Bare key=value or single-dash args
        // would otherwise trap when we offset two characters past the start.
        guard arg.hasPrefix("--"), let eq = arg.firstIndex(of: "=") else { continue }
        let k = String(arg[arg.index(arg.startIndex, offsetBy: 2)..<eq])
        let v = String(arg[arg.index(after: eq)...])
        switch k {
        case "port": if let n = Int(v) { o.port = n }
        case "token": o.token = v
        case "always-on-top": o.alwaysOnTop = (v == "true" || v == "1")
        default: break
        }
    }
    return o
}

// MARK: - WKWebView delegate (logs and auto-recovers from content-process termination)
//
// Without a delegate, WKWebView shows a blank page when the underlying
// WebContent process is killed (memory pressure, sandbox violation,
// renderer crash). The helper stays running, the panel stays on screen,
// but the SPA disappears - the symptom users see as "widgets keep
// crashing/restarting": each reload cycles the SPA through layout=[]
// (briefly no rects, no widgets visible) before fetching the layout
// again. Trapping the termination signal lets us reload immediately +
// log so we know it's happening.
final class OverlayWebViewDelegate: NSObject, WKNavigationDelegate {
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        FileHandle.standardError.write("[overlay-helper] WebContent process terminated; reloading\n".data(using: .utf8)!)
        // reloadFromOrigin re-issues the URL request, bypassing any cached
        // page state that might have triggered the kill.
        if let url = webView.url {
            webView.load(URLRequest(url: url))
        } else {
            webView.reloadFromOrigin()
        }
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        FileHandle.standardError.write("[overlay-helper] navigation failed: \(error.localizedDescription)\n".data(using: .utf8)!)
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        FileHandle.standardError.write("[overlay-helper] navigation error: \(error.localizedDescription)\n".data(using: .utf8)!)
    }
}

// MARK: - NSPanel subclass that never accepts key/main status
//
// Even with .nonactivatingPanel, the panel can become "key window" once a
// click reaches it; once key, AppKit prefers it over other windows for
// subsequent input. Overriding canBecomeKey to false guarantees the panel
// is permanently non-focal: mouse events still flow through hitTest, but
// the window never holds focus, so other apps' click chains aren't
// disturbed.
final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

// MARK: - Hit-test view (gatekeeper for click-through)
//
// Custom NSView subclass that holds the most recent widget + popover rects
// reported by the SPA and overrides hitTest: so AppKit:
//   * routes a click to the WKWebView when the cursor is inside any rect
//     (so the user can interact / drag the widget normally), and
//   * returns nil otherwise (so the click falls through to whatever app
//     owns that screen pixel - desktop, Finder window, browser, etc).
//
// Made flipped so its local coordinate origin is top-left, matching the
// CSS pixel coordinates the SPA already produces in `reportLayout`. Without
// flipping we'd have to do view.frame.height - rect.y conversion every test.
final class HitTestView: NSView {
    private var rects: [CGRect] = []
    private var hasPopover: Bool = false
    private let lock = NSLock()

    var popoverActive: Bool {
        lock.lock(); defer { lock.unlock() }
        return hasPopover
    }

    override var isFlipped: Bool { true }

    // Accept the first click so widgets receive mouseDown even when the
    // overlay panel isn't the key window (it never is, by design). Without
    // this, AppKit treats the first click on an inactive-app window as an
    // activation-only gesture and the SPA's pointerdown listener never
    // fires, breaking drag entirely.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    func setRects(_ newRects: [CGRect], popoverActive: Bool) {
        lock.lock()
        rects = newRects
        hasPopover = popoverActive
        lock.unlock()
    }

    func containsPoint(_ p: CGPoint) -> Bool {
        lock.lock()
        let snapshot = rects
        lock.unlock()
        for r in snapshot {
            if r.contains(p) { return true }
        }
        return false
    }

    override func hitTest(_ point: NSPoint) -> NSView? {
        let local = convert(point, from: superview)
        lock.lock()
        let snapshot = rects
        lock.unlock()
        for r in snapshot {
            if r.contains(local) { return super.hitTest(point) }
        }
        return nil
    }
}

// MARK: - Bridge from JS -> overlay

final class OverlayBridge: NSObject, WKScriptMessageHandler {
    weak var owner: OverlayController?

    func userContentController(_ userContentController: WKUserContentController,
                               didReceive message: WKScriptMessage) {
        // Bridge payloads are JSON.stringify'd in hostBridge.ts so a single
        // string-typed body shape works on both Windows (WebView2) and macOS.
        guard let json = message.body as? String,
              let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = obj["type"] as? String
        else { return }

        switch type {
        case "bridgeReady":
            FileHandle.standardError.write("[overlay-helper] bridgeReady from \(obj["href"] ?? "?")\n".data(using: .utf8)!)
        case "setAlwaysOnTop":
            if let v = obj["value"] as? Bool {
                DispatchQueue.main.async { self.owner?.applyAlwaysOnTop(v) }
            }
        case "reportLayout":
            let widgets = (obj["widgets"] as? [[String: Any]]) ?? []
            var parsed: [CGRect] = []
            parsed.reserveCapacity(widgets.count + 1)
            for w in widgets {
                if let r = parseRect(w) { parsed.append(r) }
            }
            var hasPopover = false
            if let popover = obj["popover"] as? [String: Any], let r = parseRect(popover) {
                parsed.append(r)
                hasPopover = true
            }
            FileHandle.standardError.write("[overlay-helper] reportLayout rects=\(parsed.count) popover=\(hasPopover)\n".data(using: .utf8)!)
            DispatchQueue.main.async { [weak self] in
                guard let webView = message.webView,
                      let view = webView.window?.contentView as? HitTestView else { return }
                view.setRects(parsed, popoverActive: hasPopover)
                // Re-evaluate cursor gating + window levels on every
                // report. Popover open -> status-bar level so the modal
                // isn't covered by the user's other apps; popover close
                // -> revert to the user's always-on-top setting (desktop
                // icon level by default).
                self?.owner?.evaluateCursor()
                self?.owner?.applyLevels()
            }
        default:
            break
        }
    }

    private func parseRect(_ d: [String: Any]) -> CGRect? {
        guard let x = (d["x"] as? NSNumber)?.doubleValue,
              let y = (d["y"] as? NSNumber)?.doubleValue,
              let w = (d["w"] as? NSNumber)?.doubleValue,
              let h = (d["h"] as? NSNumber)?.doubleValue,
              w > 0, h > 0
        else { return nil }
        return CGRect(x: x, y: y, width: w, height: h)
    }
}

// MARK: - Overlay controller (one instance, owns all per-screen windows)

final class OverlayController {
    private let opts: Options
    private let bridge = OverlayBridge()
    private let processPool = WKProcessPool()
    private let webViewDelegate = OverlayWebViewDelegate()
    private var windows: [NSWindow] = []
    private var alwaysOnTop: Bool

    init(_ opts: Options) {
        self.opts = opts
        self.alwaysOnTop = opts.alwaysOnTop
        bridge.owner = self
    }

    func start() {
        rebuild()
        startCursorTracking()
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(screensChanged),
            name: NSApplication.didChangeScreenParametersNotification,
            object: nil)
    }

    // MARK: cursor-position click-through gating
    //
    // Custom hitTest returning nil DOES NOT make NSPanel.nonactivatingPanel
    // pass clicks through to the next window. AppKit captures the event at
    // the panel level when ignoresMouseEvents is false, regardless of
    // contentView hit-test. The canonical fix: track cursor movement
    // globally (NSEvent.addGlobalMonitorForEvents) and toggle
    // ignoresMouseEvents on the panel based on whether the cursor sits
    // inside any reported widget rect. The window then becomes click-eating
    // exactly when the user is over a widget, click-through everywhere
    // else.

    private var globalMouseMonitor: Any?
    private var localMouseMonitor: Any?

    private func startCursorTracking() {
        let mask: NSEvent.EventTypeMask = [
            .mouseMoved,
            .leftMouseDragged, .rightMouseDragged, .otherMouseDragged,
            .leftMouseDown, .rightMouseDown, .otherMouseDown,
        ]
        globalMouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: mask) { [weak self] event in
            guard let self = self else { return }
            self.evaluateCursor()
            // Fired only for events the helper does NOT receive (i.e. the
            // cursor is outside any rect, ignoresMouseEvents=true, click
            // went to another app). If a popover is open in any of our
            // windows, ask the SPA to dismiss - mirrors Windows region
            // mode where SetWindowRgn excludes outside areas and the
            // SPA's mousedown listener wouldn't be reached either, but
            // here we synthesize the dismiss explicitly.
            let isMouseDown = [.leftMouseDown, .rightMouseDown, .otherMouseDown].contains(event.type)
            if isMouseDown && self.anyPopoverActive() {
                self.dispatchDismissToWebViews()
            }
        }
        localMouseMonitor = NSEvent.addLocalMonitorForEvents(matching: mask) { [weak self] event in
            self?.evaluateCursor()
            return event
        }
        evaluateCursor()
    }

    private func anyPopoverActive() -> Bool {
        for w in windows {
            if (w.contentView as? HitTestView)?.popoverActive == true { return true }
        }
        return false
    }

    func evaluateCursor() {
        let mouse = NSEvent.mouseLocation
        for w in windows {
            guard let hit = w.contentView as? HitTestView else { continue }
            // Capture clicks only when the cursor is over a registered
            // rect (widget OR popover). Outside both, the click falls
            // through to whatever's underneath. Popover dismissal on
            // outside-click is handled by injecting a synthetic body
            // mousedown into the WebView from the global mouseDown
            // monitor below - the user's click still reaches the
            // underlying app AND the SPA's click-outside handler fires.
            let windowPoint = w.convertPoint(fromScreen: mouse)
            let local = hit.convert(windowPoint, from: nil)
            let captureClicks = hit.containsPoint(local)
            if w.ignoresMouseEvents != !captureClicks {
                w.ignoresMouseEvents = !captureClicks
            }
        }
    }

    private static let dismissJS = """
        (function () {
            // Synthesize a click on the document body so the SPA's
            // click-outside listeners (WidgetContextMenu, WidgetEditSheet)
            // fire their dismiss path. We use a real MouseEvent at
            // coordinates that don't hit any panel-card or popover so the
            // SPA's `keepOpenOnTarget` returns false.
            try {
                const ev = new MouseEvent('mousedown', {
                    bubbles: true,
                    cancelable: true,
                    clientX: 0,
                    clientY: 0,
                });
                document.body.dispatchEvent(ev);
            } catch (e) { }
        })();
    """

    func dispatchDismissToWebViews() {
        for w in windows {
            guard let webView = (w.contentView as? HitTestView)?.subviews.first as? WKWebView else { continue }
            webView.evaluateJavaScript(Self.dismissJS, completionHandler: nil)
        }
    }

    @objc private func screensChanged() {
        // Display hot-plug, resolution change, sleep/wake. Tear down + rebuild
        // is cheaper than reconciling per-window because the WKProcessPool is
        // shared, so the WebContent process survives the rebuild.
        rebuild()
    }

    private func rebuild() {
        for w in windows { w.orderOut(nil); w.close() }
        windows.removeAll()

        for (i, screen) in NSScreen.screens.enumerated() {
            let w = makeWindow(for: screen, monitor: i)
            windows.append(w)
        }
    }

    func applyAlwaysOnTop(_ value: Bool) {
        alwaysOnTop = value
        applyLevels()
    }

    /// Compute the window level a panel should sit at right now. The user's
    /// always-on-top toggle picks the baseline:
    ///   - true  -> .statusBar (above every app window)
    ///   - false -> kCGDesktopIconWindowLevel (between wallpaper and apps,
    ///              so any opened app window covers the widgets)
    /// While any popover (context menu / edit sheet) is open we override
    /// to .statusBar regardless of the user setting, so the modal isn't
    /// covered by the user's other apps mid-edit. Reverts on close.
    func effectiveLevel(forPopover popoverActive: Bool) -> NSWindow.Level {
        if alwaysOnTop || popoverActive {
            return .statusBar
        }
        // CGWindowLevelKey.desktopIconWindow sits at the icon stratum -
        // above the wallpaper, below normal app windows. Other apps draw
        // on top automatically; the user can still see widgets through any
        // gap they leave on the desktop.
        let raw = Int(CGWindowLevelForKey(.desktopIconWindow))
        return NSWindow.Level(rawValue: raw)
    }

    /// Re-evaluate every window's level against the current state.
    /// Called on always-on-top toggle and on popover open/close.
    func applyLevels() {
        for w in windows {
            let popover = (w.contentView as? HitTestView)?.popoverActive ?? false
            let target = effectiveLevel(forPopover: popover)
            if w.level != target { w.level = target }
        }
    }

    // MARK: window creation

    private func makeWindow(for screen: NSScreen, monitor: Int) -> NSWindow {
        let frame = screen.frame
        // NSPanel with .nonactivatingPanel is the canonical AppKit pattern
        // for input-receiving overlays that should never become the key
        // window. Plain NSWindow becomes key on first click; once it's
        // key, AppKit routes all subsequent clicks to it regardless of our
        // contentView's hitTest decisions, which is exactly the
        // "everything blocks once a widget gets focus" symptom we hit
        // with NSWindow.
        let win = OverlayPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false,
            screen: screen)
        win.becomesKeyOnlyIfNeeded = true
        win.hidesOnDeactivate = false
        win.worksWhenModal = true
        win.isOpaque = false
        win.backgroundColor = .clear
        win.hasShadow = false
        win.isMovableByWindowBackground = false
        // Hit-testing is per-rect via HitTestView.hitTest below: events
        // inside a widget rect reach the WKWebView, events outside fall
        // through to the desktop. So leave ignoresMouseEvents = false.
        win.ignoresMouseEvents = false
        win.collectionBehavior = [
            .canJoinAllSpaces,
            .stationary,
            .ignoresCycle,
            .fullScreenAuxiliary,
        ]
        win.level = effectiveLevel(forPopover: false)

        let cfg = WKWebViewConfiguration()
        cfg.processPool = processPool
        cfg.userContentController.add(bridge, name: "nexusOverlay")

        // Diagnostics: bridgeReady at document-end + 1Hz pulse so the helper
        // can tell the difference between "SPA never loaded" and "SPA loaded
        // but is paused / occluded / throttled by WebKit". The pulse stops
        // firing if WebKit decides the window is offscreen and freezes RAF.
        // One-shot bridgeReady probe so the helper can confirm the JS
        // channel is reachable at page load. No periodic pulse - those
        // were diagnostic-only and floor the log file at ~1 line / 2s.
        let probe = WKUserScript(
            source: "try { window.webkit.messageHandlers.nexusOverlay.postMessage(JSON.stringify({type:'bridgeReady',href:location.href})); } catch (e) { }",
            injectionTime: .atDocumentEnd,
            forMainFrameOnly: false)
        cfg.userContentController.addUserScript(probe)

        let webView = WKWebView(
            frame: NSRect(origin: .zero, size: frame.size),
            configuration: cfg)
        webView.autoresizingMask = [.width, .height]
        webView.navigationDelegate = webViewDelegate
        // Private but stable since 10.10. Public underPageBackgroundColor
        // alone leaves an opaque content backing on Tahoe.
        webView.setValue(false, forKey: "drawsBackground")
        // Private SPI: keep the page running at foreground priority even
        // when AppKit reports the window as occluded. Without this, WKWebView
        // sets document.visibilityState=hidden whenever the helper isn't
        // the active app (which it never is - we're LSUIElement / accessory),
        // and WebKit pauses requestAnimationFrame. The SPA's reportLayout
        // pump is RAF-driven, so without foreground priority no rects ever
        // reach our HitTestView and the entire overlay is dead-clickless.
        // Enable Safari Web Inspector (right-click in the WKWebView ->
        // "Inspect Element"). Required for diagnosing SPA bridge issues
        // without instrumenting from outside the WebView.
        if #available(macOS 13.3, *) {
            webView.isInspectable = true
        } else {
            webView.setValue(true, forKey: "_developerExtrasEnabled")
        }

        // HitTestView consults the SPA-reported rects: clicks inside any
        // rect reach the WKWebView (drag / context menu work as expected),
        // clicks outside fall through to whatever's underneath (desktop,
        // Finder, browser, etc). When rects are empty - the default state
        // before the SPA's first reportLayout arrives - hitTest returns
        // nil for everything, which means the entire overlay is
        // click-through and the user can interact with their other apps
        // unimpeded. Once the SPA reports rects, those specific patches
        // become live.
        let hit = HitTestView(frame: NSRect(origin: .zero, size: frame.size))
        hit.autoresizingMask = [.width, .height]
        hit.addSubview(webView)
        win.contentView = hit
        win.setFrame(frame, display: true)
        // orderFrontRegardless (not orderFront) is required because the
        // helper never activates - .nonactivatingPanel + LSUIElement keep
        // the app non-frontmost, and plain orderFront is a no-op when the
        // calling app isn't active. Without this, the panel exists but
        // never appears on screen even though the WKWebView reports the
        // page as visible and DOM rects are correct (the symptom: hit-test
        // gating still fires, but the user sees nothing).
        win.orderFrontRegardless()

        if let url = overlayURL(monitor: monitor) {
            webView.load(URLRequest(url: url))
        }
        return win
    }

    private func overlayURL(monitor: Int) -> URL? {
        var c = URLComponents()
        c.scheme = "http"
        c.host = "localhost"
        c.port = opts.port
        c.path = "/overlay"
        c.queryItems = [
            URLQueryItem(name: "monitor", value: String(monitor)),
            URLQueryItem(name: "token", value: opts.token),
        ]
        return c.url
    }
}

// MARK: - Panel kiosk windows (promoted-monitor panels)
//
// Fullscreen opaque windows hosting /panel/{deviceId} on monitors the user
// promoted to Nexus panels. Counterpart of nexus-overlay's
// MonitorKioskManager on Windows: reconciled against
// GET /displays/assignments, which maps stable display ids to panel device
// records. Refresh triggers: launch, a poke line on stdin from the service
// (assignment changed), and screen-parameter changes (hot-plug, resolution).

/// First click must reach the SPA even though the kiosk panel never becomes
/// key (same constraint as HitTestView on the widget overlay).
final class KioskWebView: WKWebView {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

struct KioskAssignment {
    let displayId: String
    let panelDeviceId: String
}

final class KioskController {
    private let opts: Options
    private let webViewDelegate = OverlayWebViewDelegate()
    private var kiosks: [String: (window: NSWindow, deviceId: String)] = [:]
    private var fetchInFlight = false
    private var fetchQueued = false
    private var retryScheduled = false

    init(_ opts: Options) {
        self.opts = opts
    }

    func start() {
        refresh()
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(screensChanged),
            name: NSApplication.didChangeScreenParametersNotification,
            object: nil)
    }

    @objc private func screensChanged() {
        refresh()
    }

    /// Fetch + reconcile, coalesced: a poke arriving mid-fetch queues exactly
    /// one follow-up so a burst of changes ends on fresh state. Main thread
    /// only, so no locking.
    func refresh() {
        if fetchInFlight { fetchQueued = true; return }
        fetchInFlight = true
        fetchAssignments { [weak self] items in
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.fetchInFlight = false
                if let items = items {
                    self.reconcile(items)
                } else {
                    // Transient service hiccup: keep current windows and retry
                    // shortly - without this a failed launch-time fetch would
                    // leave the promoted monitor blank with no other trigger
                    // (pokes only fire on signature change). Service death
                    // tears the helper down via stdin EOF, so the retry loop
                    // is bounded by helper lifetime.
                    FileHandle.standardError.write("[overlay-helper] assignments fetch failed; keeping kiosks, retrying in 5s\n".data(using: .utf8)!)
                    if !self.retryScheduled {
                        self.retryScheduled = true
                        DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                            self?.retryScheduled = false
                            self?.refresh()
                        }
                    }
                }
                if self.fetchQueued {
                    self.fetchQueued = false
                    self.refresh()
                }
            }
        }
    }

    private func fetchAssignments(_ completion: @escaping ([KioskAssignment]?) -> Void) {
        var c = URLComponents()
        c.scheme = "http"
        c.host = "localhost"
        c.port = opts.port
        c.path = "/displays/assignments"
        c.queryItems = [URLQueryItem(name: "token", value: opts.token)]
        guard let url = c.url else { completion(nil); return }
        let task = URLSession.shared.dataTask(with: url) { data, response, _ in
            guard let data = data,
                  let http = response as? HTTPURLResponse, http.statusCode == 200,
                  let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
                  let list = obj["assignments"] as? [[String: Any]]
            else { completion(nil); return }
            var items: [KioskAssignment] = []
            for entry in list {
                guard let displayId = entry["displayId"] as? String,
                      let deviceId = entry["panelDeviceId"] as? String,
                      !displayId.isEmpty, !deviceId.isEmpty
                else { continue }
                items.append(KioskAssignment(displayId: displayId, panelDeviceId: deviceId))
            }
            completion(items)
        }
        task.resume()
    }

    private func reconcile(_ assignments: [KioskAssignment]) {
        let screens = screensByStableId()

        // Close kiosks that lost their assignment or screen, swapped device
        // id, or whose screen geometry changed (close + respawn re-fits).
        for (displayId, entry) in kiosks {
            let want = assignments.first { $0.displayId == displayId }
            let screen = screens[displayId]
            let stale = want == nil
                || screen == nil
                || want!.panelDeviceId != entry.deviceId
                || entry.window.frame != screen!.frame
            if stale {
                entry.window.orderOut(nil)
                entry.window.close()
                kiosks.removeValue(forKey: displayId)
                FileHandle.standardError.write("[overlay-helper] kiosk closed display=\(displayId)\n".data(using: .utf8)!)
            }
        }

        for a in assignments where kiosks[a.displayId] == nil {
            guard let screen = screens[a.displayId] else {
                FileHandle.standardError.write("[overlay-helper] kiosk display=\(a.displayId) not attached; skipping\n".data(using: .utf8)!)
                continue
            }
            let win = makeKioskWindow(for: screen, deviceId: a.panelDeviceId)
            kiosks[a.displayId] = (win, a.panelDeviceId)
            FileHandle.standardError.write("[overlay-helper] kiosk opened display=\(a.displayId) device=\(a.panelDeviceId)\n".data(using: .utf8)!)
        }
    }

    // MARK: stable display ids
    //
    // MUST mirror MacDisplayBrightnessProvider.BuildStableId exactly - the
    // assignment key is produced there: "mac-{vendor:x4}-{model:x4}-{serial:x8}",
    // fallback "display-{index+1}" when all three are zero, "-{index+1}"
    // suffix on duplicates, index = CGGetOnlineDisplayList order.
    private func stableIdsByCGDisplay() -> [CGDirectDisplayID: String] {
        var ids = [CGDirectDisplayID](repeating: 0, count: 32)
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(UInt32(ids.count), &ids, &count) == .success else { return [:] }
        var seen = Set<String>()
        var result: [CGDirectDisplayID: String] = [:]
        for index in 0..<Int(count) {
            let display = ids[index]
            let vendor = CGDisplayVendorNumber(display)
            let model = CGDisplayModelNumber(display)
            let serial = CGDisplaySerialNumber(display)
            var id: String
            if vendor != 0 || model != 0 || serial != 0 {
                id = String(format: "mac-%04x-%04x-%08x", vendor, model, serial)
            } else {
                id = "display-\(index + 1)"
            }
            if !seen.insert(id).inserted {
                id = "\(id)-\(index + 1)"
            }
            result[display] = id
        }
        return result
    }

    private func screensByStableId() -> [String: NSScreen] {
        let stable = stableIdsByCGDisplay()
        var result: [String: NSScreen] = [:]
        for screen in NSScreen.screens {
            guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
            else { continue }
            if let id = stable[CGDirectDisplayID(number.uint32Value)] {
                result[id] = screen
            }
        }
        return result
    }

    // MARK: window creation

    private func makeKioskWindow(for screen: NSScreen, deviceId: String) -> NSWindow {
        let frame = screen.frame
        // Non-activating panel for the same reason as the widget overlay:
        // the kiosk must take clicks without ever stealing key/main status
        // from the user's apps (Windows parity: WS_EX_NOACTIVATE).
        let win = OverlayPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false,
            screen: screen)
        win.becomesKeyOnlyIfNeeded = true
        win.hidesOnDeactivate = false
        win.worksWhenModal = true
        win.isOpaque = true
        win.backgroundColor = .black
        win.hasShadow = false
        win.ignoresMouseEvents = false
        win.collectionBehavior = [
            .canJoinAllSpaces,
            .stationary,
            .ignoresCycle,
            .fullScreenAuxiliary,
        ]
        // Above the menu bar / app windows: a promoted monitor is owned by
        // the panel the way the Windows kiosk owns its monitor.
        win.level = .statusBar

        let cfg = WKWebViewConfiguration()
        let webView = KioskWebView(
            frame: NSRect(origin: .zero, size: frame.size),
            configuration: cfg)
        webView.autoresizingMask = [.width, .height]
        webView.navigationDelegate = webViewDelegate
        if #available(macOS 13.3, *) {
            webView.isInspectable = true
        } else {
            webView.setValue(true, forKey: "_developerExtrasEnabled")
        }
        win.contentView = webView
        win.setFrame(frame, display: true)
        win.orderFrontRegardless()

        if let url = panelURL(deviceId: deviceId) {
            webView.load(URLRequest(url: url))
        }
        return win
    }

    private func panelURL(deviceId: String) -> URL? {
        var c = URLComponents()
        c.scheme = "http"
        c.host = "localhost"
        c.port = opts.port
        c.path = "/panel/\(deviceId)"
        c.queryItems = [URLQueryItem(name: "token", value: opts.token)]
        return c.url
    }
}

// MARK: - App delegate (Accessory app, no Dock icon)

final class AppDelegate: NSObject, NSApplicationDelegate {
    let opts: Options
    var controller: OverlayController?
    var kiosk: KioskController?

    init(_ opts: Options) { self.opts = opts }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let c = OverlayController(opts)
        c.start()
        controller = c

        let k = KioskController(opts)
        k.start()
        kiosk = k

        // Stdin doubles as lifecycle + push channel: the service writes one
        // command per line ("assignments-changed" -> kiosk reconcile), and
        // EOF means the parent (nexus-service) is gone, so terminate. The
        // EOF half keeps the helper from leaking after a service crash that
        // doesn't close its child cleanly.
        DispatchQueue.global(qos: .background).async { [weak self] in
            while let line = readLine(strippingNewline: true) {
                if line == "assignments-changed" {
                    DispatchQueue.main.async { self?.kiosk?.refresh() }
                }
            }
            DispatchQueue.main.async { NSApplication.shared.terminate(nil) }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        // NSWindow cleanup is automatic. Nothing else to flush.
    }
}

// MARK: - Entrypoint

let opts = parseOptions()
let app = NSApplication.shared
app.setActivationPolicy(.accessory)   // no Dock icon, no menu bar item
let delegate = AppDelegate(opts)
app.delegate = delegate
app.run()
