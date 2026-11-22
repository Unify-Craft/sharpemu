// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Text;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Small POSIX libc shims needed by the bundled Java 25 runtime and LWJGL.
/// All handlers return ORBIS_GEN2_OK and place the libc result in RAX so the
/// generic import dispatcher does not leave an ORBIS error code in a pointer
/// or integer return value.
/// </summary>
public static class KernelPosixCompatExports
{
    private const int Einval = 22;
    private const int Efault = 14;
    private const int Eexist = 17;
    private const int Enoent = 2;
    private const int Eperm = 1;
    private const int Eopnotsupp = 102;
    private const int MaxStringLength = 4096;

    [SysAbiExport(
        Nid = "z-7revyudjM",
        ExportName = "secure_getenv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixSecureGetenv(CpuContext ctx)
    {
        // The title passes its configuration explicitly through launch.conf.
        // NULL means "not set" and avoids exposing host environment to guests.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mkawd0NA9ts",
        ExportName = "sysconf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixSysconf(CpuContext ctx)
    {
        var name = unchecked((int)ctx[CpuRegister.Rdi]);
        long value;
        switch (name)
        {
            case 0x1E: // _SC_PAGESIZE
                value = 0x1000;
                break;
            case 0x1F: // _SC_NPROCESSORS_CONF
            case 0x20: // _SC_NPROCESSORS_ONLN
                value = System.Environment.ProcessorCount;
                break;
            case 0x21: // _SC_PHYS_PAGES (best effort)
            case 0x22: // _SC_AVPHYS_PAGES (best effort)
                value = 4096;
                break;
            case 0xBC: // _SC_LEVEL1_ICACHE_LINESIZE
            case 0xBD: // _SC_LEVEL1_DCACHE_LINESIZE
            case 0xBE: // _SC_LEVEL2_CACHE_LINESIZE
            case 0xBF: // _SC_LEVEL3_CACHE_LINESIZE
                value = 0x40;
                break;
            default:
                value = 0;
                break;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "-nvxBWa0iDs",
        ExportName = "gethostname",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixGethostname(CpuContext ctx)
    {
        var buffer = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        if (buffer == 0 || length == 0)
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, Einval);
            ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var name = "PS5Craft\0"u8;
        var writeLength = (int)Math.Min((ulong)name.Length, length);
        if (!ctx.Memory.TryWrite(buffer, name[..writeLength]))
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
            ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "j-gWL6wDros",
        ExportName = "getifaddrs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixGetifaddrs(CpuContext ctx)
    {
        // Network interface enumeration is not needed for offline Minecraft.
        // Returning -1 lets callers fall back to the loopback path.
        KernelRuntimeCompatExports.TrySetErrno(ctx, Eopnotsupp);
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "N2OjwJJGjeQ",
        ExportName = "freeifaddrs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixFreeifaddrs(CpuContext ctx)
    {
        // Paired with the no-op getifaddrs above.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "q0F6yS-rCms",
        ExportName = "strcspn",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixStrcspn(CpuContext ctx)
    {
        var source = ctx[CpuRegister.Rdi];
        var reject = ctx[CpuRegister.Rsi];
        Span<byte> current = stackalloc byte[1];

        var rejectTable = new bool[256];
        for (var i = 0; i < MaxStringLength; i++)
        {
            if (!ctx.Memory.TryRead(reject + (ulong)i, current))
            {
                break;
            }

            if (current[0] == 0)
            {
                break;
            }

            rejectTable[current[0]] = true;
        }

        for (var i = 0; i < MaxStringLength; i++)
        {
            if (!ctx.Memory.TryRead(source + (ulong)i, current))
            {
                ctx[CpuRegister.Rax] = (ulong)i;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (current[0] == 0 || rejectTable[current[0]])
            {
                ctx[CpuRegister.Rax] = (ulong)i;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        ctx[CpuRegister.Rax] = MaxStringLength;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RBcs3uut1TA",
        ExportName = "strerror_r",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    [SysAbiExport(
        Nid = "+AGtNVLAXUI",
        ExportName = "getErrorString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixStrerrorR(CpuContext ctx)
    {
        var errorNumber = unchecked((int)ctx[CpuRegister.Rdi]);
        var buffer = ctx[CpuRegister.Rsi];
        var bufferLength = ctx[CpuRegister.Rdx];
        var text = errorNumber switch
        {
            1 => "Operation not permitted",
            2 => "No such file or directory",
            9 => "Bad file descriptor",
            13 => "Permission denied",
            17 => "File exists",
            22 => "Invalid argument",
            _ => $"Unknown error {errorNumber}",
        };

        if (buffer != 0 && bufferLength != 0)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            var writeLength = (int)Math.Min((ulong)bytes.Length, bufferLength);
            if (!ctx.Memory.TryWrite(buffer, bytes.AsSpan(0, writeLength)))
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, Efault);
                ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JGMio+21L4c",
        ExportName = "mkdir",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixMkdir(CpuContext ctx)
    {
        var result = KernelMemoryCompatExports.KernelMkdir(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var errno = result switch
        {
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS => Eexist,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => Eperm,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND => Enoent,
            _ => Einval,
        };
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DRGXpDDh8Ng",
        ExportName = "lstat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixLstat(CpuContext ctx) => KernelMemoryCompatExports.PosixStat(ctx);

    [SysAbiExport(
        Nid = "MZO7FXyAPU8",
        ExportName = "remove",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixRemove(CpuContext ctx) => KernelMemoryCompatExports.PosixUnlink(ctx);

    [SysAbiExport(
        Nid = "MZb0GKT3mo8",
        ExportName = "socketpair",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixSocketpair(CpuContext ctx)
    {
        // The bundled SDL/LWJGL natives only probe socketpair during startup.
        // Fail with a POSIX errno so stale guest registers are not mistaken for
        // file descriptors.
        KernelRuntimeCompatExports.TrySetErrno(ctx, Eopnotsupp);
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // libstdc++ is not shipped with the Java runtime. libSDL3 and libopenal
    // still import these two one-time C++ static initializers. Their effects
    // are process-local stream bookkeeping that the HLE console layer already
    // emulates, so provide ABI-compatible no-ops.
    [SysAbiExport(
        Nid = "sqWytnhYdEg",
        ExportName = "_ZNSt8ios_base4InitC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libstdc++")]
    public static int IosBaseInitConstructor(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vJ5Qdva9B8A",
        ExportName = "_ZNSt14basic_ofstreamIcSt11char_traitsIcEEC1Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libstdc++")]
    public static int OfstreamConstructor(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi];
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
