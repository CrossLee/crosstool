# 一爪 Windows media module

This project keeps the Windows recording and color-sampling boundaries honest:

- Recording requests model one display, one window, or one fixed region. A
  region is checked against the display that owns it and is never widened to a
  full-screen recording.
- `RecordingSessionController` owns the complete lifecycle from capability
  checks through draft promotion. It only reports `Completed` after the backend
  confirms that the MP4 container was finalized and the owned draft was moved
  into `Recordings`.
- The output profile is fixed at 30 fps H.264, with an optional 48 kHz stereo
  AAC track. Resolution, duration, file-size, and disk-space limits match the
  existing 一爪 product boundary.
- On Windows, `ScreenRecorderWindowsSessionFactory` connects
  Windows.Graphics.Capture and its Direct3D 11 frame path to Media Foundation
  H.264/AAC encoding. Display and window recordings explicitly select WGC;
  region recordings crop the owning display in physical, display-local pixels.
  Cursor inclusion is passed to the WGC source rather than painted from a
  second polling loop.
- Optional system audio uses the default WASAPI loopback source. If there is no
  render endpoint, the factory reports `SystemAudioLoopback` and `AacEncoding`
  as unavailable before a recording begins. Encoder or native-pipeline startup
  errors are surfaced through the library's real failure callback.
- `ScreenRecorderActiveRecording` checks duration, draft size and free space
  four times per second. It signals `RecordingSessionController` early enough
  to finalize and promote the MP4 before the 2-hour, 10-GiB or reserved-space
  boundary. App shutdown awaits this same finalization path; explicit cancel
  finalizes first and only then discards the owned draft.
- Every ScreenRecorderLib create, record, stop, and dispose call for a session
  runs in order on that session's dedicated background MTA worker. Native and
  ScreenRecorder capability probes are lazy and use separate bounded workers.
  A wedged native call can therefore time out without freezing the WinUI thread
  or keeping the process alive. If cleanup cannot be confirmed, or cancellation
  produced a finalized MP4, the controller preserves and surfaces the draft.
- A native completion callback is not sufficient on its own: 一爪 also
  requires a non-empty file containing top-level `ftyp`, `moov` and non-empty
  `mdat` boxes before it marks the MP4 container finalized.
- `WindowsScreenColorSampler` is a real composited-desktop pixel sampler using
  Win32 physical virtual-desktop coordinates. The App must hide 一爪 picker
  windows before sampling. `WindowsTextClipboardWriter` writes and flushes the
  selected HEX/RGB/HSL representation to the Windows clipboard.

The project multi-targets plain .NET 8 for portable state/format tests and
Windows 10 19041+ for native API compilation. Windows 11 remains the intended
runtime and manual acceptance environment. The pinned ScreenRecorderLib 7.0.0
NuGet package contains separate x64 and ARM64 C++/CLI assets; both 一爪 media
targets must be built with an explicit matching `Platform` value. The native
library also requires the matching Microsoft Visual C++ runtime and Windows
Media Foundation (Windows N/KN editions may need the Media Feature Pack).

The production factory is the default for
`new WindowsGraphicsCaptureRecordingBackend()`. The App should retain one
`RecordingSessionController` for the whole process and await its
`DisposeAsync()` before exiting so an active recording is finalized. Details of
the third-party native dependency and license are in
`windows/THIRD_PARTY_NOTICES.md`.
