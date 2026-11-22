// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelRuntimeCompatExports
{
    [ThreadStatic]
    private static byte[]? _posixSignalMask;

    [SysAbiExport(Nid = "VkTAsrZDcJ0", ExportName = "sigfillset", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSigfillset(CpuContext ctx)
    {
        Span<byte> mask = stackalloc byte[16];
        mask.Fill(0xFF);
        return ctx[CpuRegister.Rdi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], mask)
            ? ctx.SetReturn(0) : SignalMaskFailure(ctx, 14);
    }

    [SysAbiExport(Nid = "aPcyptbOiZs", ExportName = "sigprocmask", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSigprocmask(CpuContext ctx)
    {
        var how = (int)ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        var output = ctx[CpuRegister.Rdx];
        Span<byte> requested = stackalloc byte[16];
        if (source != 0 && (how is < 1 or > 3 || !ctx.Memory.TryRead(source, requested)))
            return SignalMaskFailure(ctx, how is < 1 or > 3 ? 22 : 14);
        var mask = _posixSignalMask ??= new byte[16];
        if (output != 0 && !ctx.Memory.TryWrite(output, mask))
            return SignalMaskFailure(ctx, 14);
        if (source != 0)
            for (var i = 0; i < mask.Length; i++)
                mask[i] = how switch
                {
                    1 => (byte)(mask[i] | requested[i]),
                    2 => (byte)(mask[i] & ~requested[i]),
                    _ => requested[i],
                };
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "JZKw5+Wrnaw", ExportName = "pthread_sigmask", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libc")]
    public static int PosixPthreadSigmask(CpuContext ctx)
    {
        var how = (int)ctx[CpuRegister.Rdi];
        var source = ctx[CpuRegister.Rsi];
        var output = ctx[CpuRegister.Rdx];
        if (how is < 1 or > 3)
        {
            return SignalMaskFailure(ctx, 22);
        }

        ulong requestedLow = 0;
        ulong requestedHigh = 0;
        if (source != 0 &&
            (!KernelMemoryCompatExports.TryReadUInt64Compat(ctx, source, out requestedLow) ||
             !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, source + 8, out requestedHigh)))
        {
            return SignalMaskFailure(ctx, 14);
        }

        var mask = _posixSignalMask ??= new byte[16];
        if (source != 0)
        {
            Span<byte> requested = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(requested, requestedLow);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(requested[8..], requestedHigh);
            for (var i = 0; i < mask.Length; i++)
            {
                mask[i] = how switch
                {
                    1 => (byte)(mask[i] | requested[i]),
                    2 => (byte)(mask[i] & ~requested[i]),
                    _ => requested[i],
                };
            }
        }

        if (output != 0 &&
            (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, output,
                 System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(mask)) ||
             !KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, output + 8,
                 System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(mask.AsSpan(8)))))
        {
            return SignalMaskFailure(ctx, 14);
        }

        return ctx.SetReturn(0);
    }

    private static int SignalMaskFailure(CpuContext ctx, int errno)
    {
        _ = TrySetErrno(ctx, errno);
        return ctx.SetReturn(-1);
    }
}
