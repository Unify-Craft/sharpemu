SharpEmu 9a64b45 with local PS5Craft compatibility patches

Upstream: https://github.com/SharpEmu/SharpEmu
Base commit: 9a64b459893cb98fa1510b98c6941db625b6ddff

This tree contains a source-only copy of SharpEmu plus local HLE changes used
only for PS5Craft emulator validation. The unified diff is in
patches/ps5craft-local.patch. No console artifacts are modified.

Rebuild:
  /tmp/opencode/dotnet/dotnet publish src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Release -r linux-x64 -o /tmp/opencode/sharpeemu-patched --nologo

Run:
  /tmp/opencode/sharpeemu-patched/SharpEmu --no-log-file --log-level=info /home/lexi/Desktop/UnifyCraft/PS5Craft/dist/PS5Craft-PS5/PPSA77001-Emulator/eboot.bin
