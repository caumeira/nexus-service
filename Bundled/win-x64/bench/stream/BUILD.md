# STREAM benchmark - Windows build

stream.exe is NOT distributed as a pre-built binary. Build it from source on the Windows host.

Source: https://www.cs.virginia.edu/stream/FTP/Code/stream.c

Build command (MSVC Developer Command Prompt):
  cl /O2 /openmp /Fe:stream.exe stream.c

Place stream.exe in this directory before publishing.
