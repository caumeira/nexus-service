# STREAM benchmark - Windows build

`stream.exe` is built from `stream.c` (committed here) on a Windows host with
MSVC + OpenMP. `stream.c` is McCalpin STREAM v5.10 with minimal patches for
MSVC (the upstream source is POSIX-only):

- `<unistd.h>` / `<sys/time.h>` guarded out under `_MSC_VER`
- `typedef long long ssize_t;` for MSVC (POSIX type, normally from unistd.h)
- `mysecond()` uses `omp_get_wtime()` under `_MSC_VER` (no gettimeofday on MSVC)

Source: https://www.cs.virginia.edu/stream/FTP/Code/stream.c

Build (VS 2022 Build Tools, "Desktop development with C++"):

    call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
    cl /O2 /openmp /DSTREAM_ARRAY_SIZE=60000000 /DNTIMES=40 /Fe:stream.exe stream.c

`/openmp` makes STREAM multi-threaded (defaults to all logical cores) and links
`vcomp140.dll` - committed alongside `stream.exe` so the benchmark runs on hosts
without the VC++ OpenMP runtime installed.

`STREAM_ARRAY_SIZE=60000000` = 480 MB per array (3 arrays), well above any
current desktop LLC so the Triad rate reflects DRAM bandwidth, not cache.
`NTIMES=40` reports best-of-40 (bandwidth saturates immediately, so this is
for run-to-run stability, not duration).
