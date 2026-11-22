// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// POSIX math and scheduler shims used by the bundled LWJGL natives. These
/// libraries keep plain FreeBSD import names, so the export-name index maps
/// them onto these canonical NIDs during import setup.
/// </summary>
public static class KernelPosixMathCompatExports
{
    private const int Enosys = 102;

    [SysAbiExport(
        Nid = "H8ya2H00jbI",
        ExportName = "sin",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libm")]
    public static int PosixSin(CpuContext ctx)
    {
        SetXmmDouble(ctx, 0, Math.Sin(GetXmmDouble(ctx, 0)));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "2WE3BTYVwKM",
        ExportName = "cos",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libm")]
    public static int PosixCos(CpuContext ctx)
    {
        SetXmmDouble(ctx, 0, Math.Cos(GetXmmDouble(ctx, 0)));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jMB7EFyu30Y",
        ExportName = "sincos",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libm")]
    public static int PosixSinCos(CpuContext ctx)
    {
        var value = GetXmmDouble(ctx, 0);
        var sinAddress = ctx[CpuRegister.Rdi];
        var cosAddress = ctx[CpuRegister.Rsi];
        if (sinAddress != 0)
            _ = ctx.Memory.TryWrite(sinAddress, BitConverter.GetBytes(Math.Sin(value)));
        if (cosAddress != 0)
            _ = ctx.Memory.TryWrite(cosAddress, BitConverter.GetBytes(Math.Cos(value)));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "TYE4irxSmko",
        ExportName = "toupper",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixToupper(CpuContext ctx)
    {
        var value = unchecked((int)ctx[CpuRegister.Rdi]);
        if (value is >= 'a' and <= 'z')
            value -= 'a' - 'A';
        ctx[CpuRegister.Rax] = unchecked((ulong)(uint)value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Jahsnh4KKkg",
        ExportName = "madvise",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixMadvise(CpuContext ctx)
    {
        // Guest mappings are host-backed and committed up front, so the advice
        // is advisory only. Return POSIX success instead of an ORBIS error.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LBQrZ9dSuqI",
        ExportName = "sched_getaffinity",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixSchedGetaffinity(CpuContext ctx)
    {
        var maskAddress = ctx[CpuRegister.Rdx];
        var maskSize = (int)Math.Min(ctx[CpuRegister.Rsi], 4096);
        if (maskAddress == 0 || maskSize == 0)
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, 22);
            ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var mask = new byte[maskSize];
        var cpuCount = Math.Min(Environment.ProcessorCount, maskSize * 8);
        for (var cpu = 0; cpu < cpuCount; cpu++)
            mask[cpu / 8] |= (byte)(1 << (cpu % 8));

        if (!ctx.Memory.TryWrite(maskAddress, mask))
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, 14);
            ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "NYlzGdLko7I",
        ExportName = "__sched_cpucount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixSchedCpuCount(CpuContext ctx)
    {
        var maskAddress = ctx[CpuRegister.Rsi];
        var maskSize = (int)Math.Min(ctx[CpuRegister.Rdi], 4096);
        if (maskAddress == 0 || maskSize == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var mask = new byte[maskSize];
        if (!ctx.Memory.TryRead(maskAddress, mask))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var count = 0;
        foreach (var value in mask)
        {
            var current = value;
            while (current != 0)
            {
                count += current & 1;
                current >>= 1;
            }
        }

        ctx[CpuRegister.Rax] = (ulong)count;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "voppQS6Gtuk",
        ExportName = "__register_atfork",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixRegisterAtfork(CpuContext ctx)
    {
        // Guest threads are cooperatively scheduled and never fork the host.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "-Jp7F+pXxNg",
        ExportName = "pipe",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixPipe(CpuContext ctx)
    {
        // No anonymous host pipe bridge is exposed to the guest yet. Report a
        // normal POSIX failure instead of leaving an ORBIS error in the fd out
        // parameters.
        KernelRuntimeCompatExports.TrySetErrno(ctx, Enosys);
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static double GetXmmDouble(CpuContext ctx, int registerIndex)
    {
        ctx.GetXmmRegister(registerIndex, out var bits, out _);
        return BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    private static void SetXmmDouble(CpuContext ctx, int registerIndex, double value)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        ctx.SetXmmRegister(registerIndex, bits, 0);
    }
}
