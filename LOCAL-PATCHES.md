# SharpEmu 9a64b45 with local PS5Craft compatibility patches

Upstream: https://github.com/SharpEmu/SharpEmu
Base commit: 9a64b459893cb98fa1510b98c6941db625b6ddff

This tree contains a source-only SharpEmu checkout plus local HLE/loader changes
used for PS5Craft emulator validation. The source snapshot was imported from
upstream `9a64b45` as local commit `2b80259`; the canonical unified diff
`patches/ps5craft-local.patch` is generated against that local import commit
and includes the two new POSIX compatibility source files. These changes are
emulator-side only; no console artifact, signed FSELF, or pristine runtime file
is modified.

Rebuild:

```sh
/tmp/opencode/dotnet/dotnet publish \
  src/SharpEmu.CLI/SharpEmu.CLI.csproj \
  -c Release -r linux-x64 -o /tmp/opencode/sharpeemu-patched --nologo
```

Run:

```sh
/tmp/opencode/sharpeemu-patched/SharpEmu \
  --no-log-file --log-level=info \
  /home/lexi/Desktop/UnifyCraft/PS5Craft/dist/PS5Craft-PS5/PPSA77001-Emulator/eboot.bin
```

## What the patches do

- **JVM/JDK import loading**: NID-aware import patching, plain export-name
  fallback, version-suffix handling, and defined-zero-export support in the
  ELF loader. Dynamically loaded JDK libraries are accepted as guest modules.
- **Dynamic-loader bridge**: dedicated POSIX-ABI HLE shims
  `ps5craft_dlopen`/`ps5craft_dlsym`. `dlopen(NULL, ...)` returns the global
  handle `0x7F5C000000000001`; `dlsym` accepts handle `0` as a global lookup.
  Only loaded guest modules and registered runtime symbols are searched.
- **Filesystem compatibility**: POSIX `stat`/`lstat`/`access`/`open`/`realpath`
  shims, writable-path handling for `/download0` and `/temp0`, cached-negative
  stat fixes, guest-path containment, `mkdir`/`remove` support, and a
  `/` + `O_DIRECTORY` failure that sends JDK 25's `realpath0` to the HLE
  `realpath(path, resolved)` fallback.
- **Entropy/stdio/system data**: `kern.rng_pseudo` `sysctlbyname` export and
  synthesized libc `FILE*` data symbols so bundled libc imports are non-null.
- **Threading/runtime compatibility**: pthread and scheduler shims, mutex result
  mapping, C++ constructor/no-op exports, `sleep`, signal handling, `sysconf`,
  `gethostname`, `getifaddrs`/`freeifaddrs`, `strerror_r`, `strcspn`,
  `secure_getenv`, POSIX math exports, and guest `environ` support.
- **Module startup**: guest `.init_array` execution and `kernel_mprotect`
  handling for JVM code-cache initialization.
- **Graphics-adjacent startup**: allowlists `SDL_VIDEODRIVER` in HLE `getenv`
  so the bundled SDL3 PS5 video driver initializes. This only makes the window
  appear; it does not implement desktop OpenGL/Vulkan passthrough.

## Validation status

A bounded clean run reaches `JNI_CreateJavaVM`, Minecraft's LWJGL 3.4.3+4
backend and `SDL3 window ready: mode=Windowed size=1920x1080`, and keeps the
host Vulkan swapchain alive. The run is then terminated cleanly with zero GPU
submissions and zero shader programs. SharpEmu has no generic host
`libGL`/EGL/Vulkan import passthrough; its renderer consumes PS5
`sceVideoOut*` plus AGC/GNM command streams. Rendering Minecraft therefore needs
a dedicated OpenGL-to-PS5-GPU HLE layer or a native PS5 renderer, neither of
which is included here.

The reference ProsperoCraft PS5 LWJGL/Zink JARs were investigated but rejected
for this title: copying `lwjgl-3.4.3-ps5-modules.jar` did not produce a frame,
and adding `lwjgl-3.4.3-ps5-platform.jar` caused a native-memory abort. The
reference classpath audits explicitly cover classpath verification only, not
native initialization or PS5 execution.

Temporary filesystem/path debug instrumentation and module-init diagnostics were
removed before refreshing this patch dump.
