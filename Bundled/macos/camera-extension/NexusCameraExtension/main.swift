// Entry point. sysextd launches this executable via the mach service named in
// Info.plist; startService publishes the provider to the CMIO DAL assistant.

import CoreMediaIO
import Foundation

private func infoPlistUUID(_ key: String) -> UUID {
    guard let raw = Bundle.main.object(forInfoDictionaryKey: key) as? String,
          let uuid = UUID(uuidString: raw)
    else {
        fatalError("Missing or malformed \(key) in Info.plist")
    }
    return uuid
}

let providerSource = NexusCameraProviderSource(
    clientQueue: nil,
    deviceUUID: infoPlistUUID("NexusCameraDeviceUUID"),
    sourceUUID: infoPlistUUID("NexusCameraSourceUUID"),
    sinkUUID: infoPlistUUID("NexusCameraSinkUUID")
)

CMIOExtensionProvider.startService(provider: providerSource.provider)
CFRunLoopRun()
