// nexus-camera-activator
//
// Dylib the Nexus service loads in-process (P/Invoke) to submit
// OSSystemExtension requests for the bundled camera extension.
//
// It MUST run inside a process whose main bundle contains the extension at
// Contents/Library/SystemExtensions; the .NET AOT binary at
// Nexus.app/Contents/MacOS/Nexus satisfies that because Bundle.main resolves
// to Nexus.app. Do not host this code in a sidecar helper: those embed their
// own __info_plist, which gives the process a different main-bundle identity
// that sysextd may reject (see camera-extension/NOTES.md).
//
// Both entry points block the calling thread until a terminal delegate
// callback, a needs-approval callback, or a timeout; the service is expected
// to call them from a worker thread. Return codes in NOTES-HELPER.md.

import Foundation
import SystemExtensions

// Must match PRODUCT_BUNDLE_IDENTIFIER in camera-extension/project.yml.
private let extensionIdentifier = "com.hellonexus.panel.service.camera-extension"

// Sentinel codes shared by both entry points.
private let codeTimedOut: Int32 = 90
private let codeBusy: Int32 = 91
private let codeUnknownError: Int32 = -100

// nexus_camera_extension_activate results.
private let codeActivated: Int32 = 0
private let codeRebootRequired: Int32 = 1
private let codeNeedsApproval: Int32 = 2

// nexus_camera_extension_status results.
private let codeEnabled: Int32 = 0
private let codeAwaitingApproval: Int32 = 1
private let codeInstalledDisabled: Int32 = 2
private let codeNotInstalled: Int32 = 3

private let activationTimeoutSeconds = 60
private let statusTimeoutSeconds = 15

private let callbackQueue = DispatchQueue(label: "com.hellonexus.panel.service.camera-activator")
private let apiLock = NSLock()
// OSSystemExtensionRequest holds its delegate weakly; pin the live pair so a
// callback arriving after a timeout lands on a valid object.
private var pinnedRequest: (OSSystemExtensionRequest, BlockingDelegate)?

private class BlockingDelegate: NSObject, OSSystemExtensionRequestDelegate {
    private let semaphore = DispatchSemaphore(value: 0)
    private let resultLock = NSLock()
    private var result: Int32?

    // First terminal-ish callback wins; later ones are ignored.
    func resolve(_ code: Int32) {
        resultLock.lock()
        let first = result == nil
        if first { result = code }
        resultLock.unlock()
        if first { semaphore.signal() }
    }

    func waitForResult(seconds: Int) -> Int32 {
        _ = semaphore.wait(timeout: .now() + .seconds(seconds))
        resultLock.lock()
        defer { resultLock.unlock() }
        return result ?? codeTimedOut
    }

    func request(
        _ request: OSSystemExtensionRequest,
        actionForReplacingExtension existing: OSSystemExtensionProperties,
        withExtension ext: OSSystemExtensionProperties
    ) -> OSSystemExtensionRequest.ReplacementAction {
        // CFBundleVersion must rise between releases or sysextd skips the swap.
        .replace
    }

    func requestNeedsUserApproval(_ request: OSSystemExtensionRequest) {
        // Not terminal: the request stays pending in sysextd until the user
        // approves in System Settings. Resolve now so the caller can surface
        // guidance immediately and poll status afterwards.
        resolve(codeNeedsApproval)
    }

    func request(
        _ request: OSSystemExtensionRequest,
        didFinishWithResult result: OSSystemExtensionRequest.Result
    ) {
        switch result {
        case .completed: resolve(codeActivated)
        case .willCompleteAfterReboot: resolve(codeRebootRequired)
        @unknown default: resolve(codeUnknownError)
        }
    }

    func request(_ request: OSSystemExtensionRequest, didFailWithError error: Error) {
        let nsError = error as NSError
        resolve(nsError.domain == OSSystemExtensionErrorDomain ? -Int32(nsError.code) : codeUnknownError)
    }
}

private final class StatusDelegate: BlockingDelegate {
    func request(
        _ request: OSSystemExtensionRequest,
        foundProperties properties: [OSSystemExtensionProperties]
    ) {
        // Several versions can be staged at once (pending replacement); report
        // the most-installed state across them.
        if properties.contains(where: { $0.isEnabled }) {
            resolve(codeEnabled)
        } else if properties.contains(where: { $0.isAwaitingUserApproval }) {
            resolve(codeAwaitingApproval)
        } else if properties.isEmpty {
            resolve(codeNotInstalled)
        } else {
            resolve(codeInstalledDisabled)
        }
    }

    override func request(
        _ request: OSSystemExtensionRequest,
        didFinishWithResult result: OSSystemExtensionRequest.Result
    ) {
        // A properties request that finishes without a foundProperties
        // callback carries no install state.
        resolve(codeNotInstalled)
    }
}

private func runBlocking(_ delegate: BlockingDelegate, _ request: OSSystemExtensionRequest, timeoutSeconds: Int) -> Int32 {
    guard apiLock.try() else { return codeBusy }
    defer { apiLock.unlock() }
    request.delegate = delegate
    pinnedRequest = (request, delegate)
    OSSystemExtensionManager.shared.submitRequest(request)
    return delegate.waitForResult(seconds: timeoutSeconds)
}

@_cdecl("nexus_camera_extension_activate")
public func nexus_camera_extension_activate() -> Int32 {
    runBlocking(
        BlockingDelegate(),
        OSSystemExtensionRequest.activationRequest(
            forExtensionWithIdentifier: extensionIdentifier, queue: callbackQueue
        ),
        timeoutSeconds: activationTimeoutSeconds
    )
}

@_cdecl("nexus_camera_extension_status")
public func nexus_camera_extension_status() -> Int32 {
    runBlocking(
        StatusDelegate(),
        OSSystemExtensionRequest.propertiesRequest(
            forExtensionWithIdentifier: extensionIdentifier, queue: callbackQueue
        ),
        timeoutSeconds: statusTimeoutSeconds
    )
}
