// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Positional and scatter/gather file I/O, synchronization, renaming, fcntl,
/// polling and asynchronous I/O (AIO) that complement the base file exports.
/// These share the same guest FD table and path-resolution helpers via the
/// partial <see cref="KernelMemoryCompatExports"/> class.
/// </summary>
public static partial class KernelMemoryCompatExports
{
    // fcntl commands (FreeBSD numbering used by the PS4/PS5 libc).
    private const int F_DUPFD = 0;
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;

    // poll event bits.
    private const short POLLIN = 0x0001;
    private const short POLLOUT = 0x0004;

    // AIO request state (SCE_KERNEL_AIO_STATE_*).
    private const uint AioStateCompleted = 3;
    private const int SizeofAioRwRequest = 40;

    private static long _nextAioSubmitId = 1;
    private static readonly ConcurrentDictionary<uint, int> _aioResults = new();
    private static ulong _qsortStackAddress;
    private const ulong QsortStackSize = 0x40000;

    private static Stream? GetOpenFile(int fd)
    {
        lock (_fdGate)
        {
            return _openFiles.TryGetValue(fd, out var stream) ? stream : null;
        }
    }

    // A descriptor is a host file or a read-only file served from a mounted archive.
    private static int ReadOpenFileAt(Stream stream, Span<byte> buffer, long offset) => stream switch
    {
        FileStream file => RandomAccess.Read(file.SafeFileHandle, buffer, offset),
        ZArchiveFileStream archived => archived.ReadAt(buffer, offset),
        _ => throw new IOException("The descriptor does not support positional reads."),
    };

    private static void WriteOpenFileAt(Stream stream, ReadOnlySpan<byte> payload, long offset)
    {
        if (stream is not FileStream file)
        {
            throw new IOException("The descriptor is read-only.");
        }

        RandomAccess.Write(file.SafeFileHandle, payload, offset);
    }

    private static void FlushOpenFileToDisk(Stream stream)
    {
        if (stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
    }

    // ---- Positional read/write (do not move the file offset) ----

    [SysAbiExport(Nid = "ezv-RSBNKqI", ExportName = "pread",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPread(CpuContext ctx) => KernelPreadCore(ctx);

    [SysAbiExport(Nid = "+r3rMFwItV4", ExportName = "sceKernelPread",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelPread(CpuContext ctx) => KernelPreadCore(ctx);

    // Emulator-only helper used by the patched libjimage mmap wrapper. The
    // prebuilt jimage library maps its modules image with raw FreeBSD syscalls;
    // a plain malloc-backed stub gives it writable memory but leaves the class
    // data uninitialized. This export allocates host-backed guest memory and
    // fills it with a positional read from the original descriptor.
#pragma warning disable SHEM006
    [SysAbiExport(Nid = "dHeqeJdz8yY", ExportName = "ps5craft_mmap_file",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
#pragma warning restore SHEM006
    public static int JimageMmapFile(CpuContext ctx)
    {
        var length = ctx[CpuRegister.Rsi];
        var fd = unchecked((int)ctx[CpuRegister.R8]);
        var offset = unchecked((long)ctx[CpuRegister.R9]);        if (length == 0 || length > int.MaxValue || offset < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Stream? stream = null;
        if (fd >= 0)
        {
            stream = GetOpenFile(fd);
            if (stream is null)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }
        }

        unsafe
        {
            var address = (ulong)HostMemory.Alloc(
                null,
                (nuint)length,
                HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
                HostMemory.PAGE_READWRITE);
            if (address == 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED;
            }

            if (stream is not null)
            {
                // mmap zero-fills any part of the final page not covered by
                // the file. HotSpot parses headers and padding from that tail,
                // so an uninitialized buffer is not a valid mmap emulation.
                var buffer = new byte[(int)length];
                int read;
                try
                {
                    read = ReadOpenFileAt(stream, buffer, offset);
                }
                catch (IOException)
                {
                    _ = HostMemory.Free((void*)address, (nuint)length, HostMemory.MEM_RELEASE);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                if (!TryWriteCompat(ctx, address, buffer))
                {
                    _ = HostMemory.Free((void*)address, (nuint)length, HostMemory.MEM_RELEASE);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }
            }
            else if (!TryWriteCompat(ctx, address, new byte[(int)length]))
            {
                _ = HostMemory.Free((void*)address, (nuint)length, HostMemory.MEM_RELEASE);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            ctx[CpuRegister.Rax] = address;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int KernelPreadCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var offset = unchecked((long)ctx[CpuRegister.Rcx]);
        if (requested < 0 || (requested > 0 && bufferAddress == 0) || offset < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (requested == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(requested);
        int read;
        try
        {
            read = ReadOpenFileAt(stream, buffer, offset);
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (read > 0 && !TryWriteCompat(ctx, bufferAddress, buffer.AsSpan(0, read)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)read);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // jimage resolves ZIP_InflateFully from libzip. libzip uses raw FreeBSD
    // syscalls and cannot be loaded by SharpEmu, so provide the same zlib-stream
    // decompression through the managed runtime. Signature:
    // jboolean ZIP_InflateFully(void* in, jlong inLen, void* out, jlong outLen,
    //                           char** pmsg).
#pragma warning disable SHEM006
    [SysAbiExport(Nid = "0tRxq1I82CA", ExportName = "ZIP_InflateFully",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
#pragma warning restore SHEM006
    public static int ZipInflateFully(CpuContext ctx)
    {
        var errorPointer = ctx[CpuRegister.R8];
        if (errorPointer != 0)
        {
            _ = TryWriteUInt64Compat(ctx, errorPointer, 0);
        }

        var inputAddress = ctx[CpuRegister.Rdi];
        var inputLength = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        var outputLength = ctx[CpuRegister.Rcx];
        if (inputAddress == 0 || outputAddress == 0 ||
            inputLength == 0 || inputLength > int.MaxValue ||
            outputLength == 0 || outputLength > int.MaxValue)
        {
            return 0;
        }

        var compressed = GC.AllocateUninitializedArray<byte>((int)inputLength);
        if (!TryReadCompat(ctx, inputAddress, compressed))
        {
            return 0;
        }

        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var output = GC.AllocateUninitializedArray<byte>((int)outputLength);
            var total = 0;
            while (total < output.Length)
            {
                var read = zlib.Read(output, total, output.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            // ZIP_InflateFully requires the stream to complete at the exact
            // declared output size and rejects trailing compressed data.
            var trailing = zlib.ReadByte();            if (total != output.Length || trailing != -1)
            {
                return 0;
            }

            return TryWriteCompat(ctx, outputAddress, output) ? 1 : 0;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    // HotSpot sorts VM metadata and InstanceKlass field tables with qsort.
    // Leaving the import unresolved silently leaves those tables unsorted,
    // which later breaks binary-search field lookups (for example
    // java.lang.Class.classRedefinedCount). Invoke the guest comparator for
    // each pair and keep guest memory coherent while Array.Sort reorders the
    // managed copy.
#pragma warning disable SHEM006
    [SysAbiExport(Nid = "AEJdIVZTEmo", ExportName = "qsort",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libc")]
#pragma warning restore SHEM006
    public static int GuestQsort(CpuContext ctx)
    {
        var baseAddress = ctx[CpuRegister.Rdi];
        var count = (int)Math.Min(ctx[CpuRegister.Rsi], int.MaxValue);
        var elementSize = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var comparator = ctx[CpuRegister.Rcx];
        if (count < 0 || elementSize <= 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
        if (count < 2 || baseAddress == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        if (count > int.MaxValue / elementSize)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var elements = GC.AllocateUninitializedArray<byte>(count * elementSize);
        if (!TryReadCompat(ctx, baseAddress, elements) ||
            !TryWriteCompat(ctx, baseAddress, elements))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var failures = 0;
        var scratch = GC.AllocateUninitializedArray<byte>(elementSize);
        var comparatorVaddr = comparator >= 0x200_0000UL ? comparator - 0x200_0000UL : comparator;
        int Compare(int left, int right)
        {
            var leftAddress = baseAddress + (ulong)(left * elementSize);
            var rightAddress = baseAddress + (ulong)(right * elementSize);
            if (!TryWriteCompat(ctx, leftAddress, elements.AsSpan(left * elementSize, elementSize)) ||
                !TryWriteCompat(ctx, rightAddress, elements.AsSpan(right * elementSize, elementSize)))
            {
                failures++;
                return CompareBytes(elements, left * elementSize, elements, right * elementSize, elementSize);
            }

            if (TryCompareKnownHotSpotComparator(ctx, comparatorVaddr, leftAddress, rightAddress, out var knownResult))
            {
                return knownResult;
            }

            var scheduler = GuestThreadExecution.Scheduler;
            if (scheduler is null || comparator < 0x10000)
            {
                return CompareBytes(elements, left * elementSize, elements, right * elementSize, elementSize);
            }

            if (!scheduler.TryCallGuestFunction(
                    ctx,
                    comparator,
                    leftAddress,
                    rightAddress,
                    0,
                    0,
                    0,
                    "qsort",
                    out var result,
                    out var error))
            {
                return CompareBytes(elements, left * elementSize, elements, right * elementSize, elementSize);
            }

            return unchecked((int)(long)result);
        }

        void Swap(int left, int right)
        {
            if (left == right)
            {
                return;
            }

            var leftSpan = elements.AsSpan(left * elementSize, elementSize);
            var rightSpan = elements.AsSpan(right * elementSize, elementSize);
            leftSpan.CopyTo(scratch);
            rightSpan.CopyTo(leftSpan);
            scratch.CopyTo(rightSpan);
            TryWriteCompat(ctx, baseAddress + (ulong)(left * elementSize), leftSpan);
            TryWriteCompat(ctx, baseAddress + (ulong)(right * elementSize), rightSpan);
        }

        void SiftDown(int root, int end)
        {
            while (true)
            {
                var child = root * 2 + 1;
                if (child >= end)
                {
                    return;
                }
                if (child + 1 < end && Compare(child, child + 1) < 0)
                {
                    child++;
                }
                if (Compare(root, child) >= 0)
                {
                    return;
                }
                Swap(root, child);
                root = child;
            }
        }

        // Heapsort keeps the guest array coherent after every swap. HotSpot's
        // comparators dereference the element pointers in guest memory, so a
        // managed permutation that only writes the final result is not enough.
        for (var i = count / 2 - 1; i >= 0; i--)
        {
            SiftDown(i, count);
        }
        for (var end = count - 1; end > 0; end--)
        {
            Swap(0, end);
            SiftDown(0, end);
        }
        if (!TryWriteCompat(ctx, baseAddress, elements))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static ulong EnsureQsortStack()
    {
        if (_qsortStackAddress != 0)
        {
            return _qsortStackAddress;
        }

        unsafe
        {
            var address = (ulong)HostMemory.Alloc(
                null,
                (nuint)QsortStackSize,
                HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
                HostMemory.PAGE_READWRITE);
            _qsortStackAddress = address;
            return address;
        }
    }

    private static bool TryCompareKnownHotSpotComparator(
        CpuContext ctx,
        ulong comparatorVaddr,
        ulong leftAddress,
        ulong rightAddress,
        out int result)
    {
        result = 0;
        switch (comparatorVaddr)
        {
            case 0xDE3060UL: // compare_vmsymbol_sid
            {
                if (!TryReadUInt32Compat(ctx, leftAddress, out var leftSid) ||
                    !TryReadUInt32Compat(ctx, rightAddress, out var rightSid) ||
                    !TryReadUInt64Compat(ctx, comparatorVaddr - 0xDE3060UL + 0x118E938UL, out var symbolTable))
                {
                    return false;
                }
                if (!TryReadUInt64Compat(ctx, symbolTable + leftSid * 8UL, out var leftSymbol) ||
                    !TryReadUInt64Compat(ctx, symbolTable + rightSid * 8UL, out var rightSymbol))
                {
                    return false;
                }
                result = leftSymbol < rightSymbol ? -1 : leftSymbol > rightSymbol ? 1 : 0;
                return true;
            }
            case 0x4BDB30UL: // LayoutRawBlock::compare_size_inverted
            {
                if (!TryReadUInt64Compat(ctx, leftAddress, out var leftBlock) ||
                    !TryReadUInt64Compat(ctx, rightAddress, out var rightBlock) ||
                    !TryReadUInt32Compat(ctx, leftBlock + 0x1CUL, out var leftSize) ||
                    !TryReadUInt32Compat(ctx, rightBlock + 0x1CUL, out var rightSize))
                {
                    return false;
                }
                var sizeDiff = unchecked((int)leftSize) - unchecked((int)rightSize);
                if (sizeDiff != 0)
                {
                    result = sizeDiff;
                    return true;
                }
                if (!TryReadUInt32Compat(ctx, rightBlock + 0x20UL, out var rightOffset) ||
                    !TryReadUInt32Compat(ctx, leftBlock + 0x20UL, out var leftOffset))
                {
                    return false;
                }
                result = unchecked((int)rightOffset) - unchecked((int)leftOffset);
                return true;
            }
            case 0x4BE9F0UL: // LayoutRawBlock::compare_offset
            {
                if (!TryReadUInt64Compat(ctx, leftAddress, out var leftBlock) ||
                    !TryReadUInt64Compat(ctx, rightAddress, out var rightBlock) ||
                    !TryReadUInt32Compat(ctx, leftBlock + 0x14UL, out var leftOffset) ||
                    !TryReadUInt32Compat(ctx, rightBlock + 0x14UL, out var rightOffset))
                {
                    return false;
                }
                result = unchecked((int)leftOffset) - unchecked((int)rightOffset);
                return true;
            }
            case 0x4BD6B0UL: // FieldInfoStream search-table comparator
            {
                if (!TryReadUInt64Compat(ctx, leftAddress, out var leftName) ||
                    !TryReadUInt64Compat(ctx, rightAddress, out var rightName))
                {
                    return false;
                }
                if (leftName != rightName)
                {
                    result = leftName < rightName ? -1 : 1;
                    return true;
                }
                if (!TryReadUInt64Compat(ctx, leftAddress + 8UL, out var leftSig) ||
                    !TryReadUInt64Compat(ctx, rightAddress + 8UL, out var rightSig))
                {
                    return false;
                }
                result = leftSig < rightSig ? -1 : leftSig > rightSig ? 1 : 0;
                return true;
            }
            default:
                return false;
        }
    }

    private static int CompareBytes(byte[] left, int leftOffset, byte[] right, int rightOffset, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var diff = left[leftOffset + i].CompareTo(right[rightOffset + i]);
            if (diff != 0)
            {
                return diff;
            }
        }
        return 0;
    }

    [SysAbiExport(Nid = "C2kJ-byS5rM", ExportName = "pwrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPwrite(CpuContext ctx) => KernelPwriteCore(ctx);

    [SysAbiExport(Nid = "nKWi-N2HBV4", ExportName = "sceKernelPwrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelPwrite(CpuContext ctx) => KernelPwriteCore(ctx);

    private static int KernelPwriteCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var offset = unchecked((long)ctx[CpuRegister.Rcx]);
        if (requested < 0 || (requested > 0 && bufferAddress == 0) || offset < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (requested == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var payload = GC.AllocateUninitializedArray<byte>(requested);
        if (!TryReadCompat(ctx, bufferAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        try
        {
            WriteOpenFileAt(stream, payload, offset);
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)requested);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Synchronization ----

    [SysAbiExport(Nid = "juWbTNM+8hw", ExportName = "fsync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFsync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "fTx66l5iWIA", ExportName = "sceKernelFsync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFsync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "KIbJFQ0I1Cg", ExportName = "fdatasync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFdatasync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "30Rh4ixbKy4", ExportName = "sceKernelFdatasync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFdatasync(CpuContext ctx) => KernelFsyncCore(ctx);

    private static int KernelFsyncCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            if (fd is 0 or 1 or 2)
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            FlushOpenFileToDisk(stream);
        }
        catch (IOException)
        {
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "Y2OqwJQ3lr8", ExportName = "sync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSync(CpuContext ctx)
    {
        lock (_fdGate)
        {
            foreach (var stream in _openFiles.Values)
            {
                try { FlushOpenFileToDisk(stream); } catch (IOException) { }
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Truncation ----

    [SysAbiExport(Nid = "ih4CD9-gghM", ExportName = "ftruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFtruncate(CpuContext ctx) => KernelFtruncateCore(ctx);

    [SysAbiExport(Nid = "VW3TVZiM4-E", ExportName = "sceKernelFtruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFtruncate(CpuContext ctx) => KernelFtruncateCore(ctx);

    private static int KernelFtruncateCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var length = unchecked((long)ctx[CpuRegister.Rsi]);
        if (length < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            stream.SetLength(length);
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "ayrtszI7GBg", ExportName = "truncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixTruncate(CpuContext ctx) => KernelTruncateCore(ctx);

    [SysAbiExport(Nid = "WlyEA-sLDf0", ExportName = "sceKernelTruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelTruncate(CpuContext ctx) => KernelTruncateCore(ctx);

    private static int KernelTruncateCore(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var length = unchecked((long)ctx[CpuRegister.Rsi]);
        if (length < 0 || !TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (string.IsNullOrEmpty(hostPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            using var stream = new FileStream(hostPath, FileMode.Open, FileAccess.Write, GuestFileShare);
            stream.SetLength(length);
        }
        catch (FileNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Rename ----

    [SysAbiExport(Nid = "NN01qLRhiqU", ExportName = "rename",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixRename(CpuContext ctx) => KernelRenameCore(ctx);

    [SysAbiExport(Nid = "52NcYU9+lEo", ExportName = "sceKernelRename",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelRename(CpuContext ctx) => KernelRenameCore(ctx);

    private static int KernelRenameCore(CpuContext ctx)
    {
        if (!TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rdi], MaxGuestStringLength, out var fromGuest) ||
            !TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rsi], MaxGuestStringLength, out var toGuest))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (IsReadOnlyGuestMutationPath(fromGuest) || IsReadOnlyGuestMutationPath(toGuest))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        var fromHost = ResolveGuestPath(fromGuest);
        var toHost = ResolveGuestPath(toGuest);
        if (string.IsNullOrEmpty(fromHost) || string.IsNullOrEmpty(toHost))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            if (Directory.Exists(fromHost))
            {
                Directory.Move(fromHost, toHost);
            }
            else
            {
                File.Move(fromHost, toHost, overwrite: true);
            }
        }
        catch (FileNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (DirectoryNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        InvalidateNegativeStatCacheForPathAndAncestors(toGuest);
        AddNegativeStatCacheForGuestPath(fromGuest);
        InvalidateAprFileSizeCache(fromHost);
        InvalidateAprFileSizeCache(toHost);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Descriptor duplication ----

    [SysAbiExport(Nid = "iiQjzvfWDq0", ExportName = "dup",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixDup(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(fd, out var stream))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            // POSIX dup shares the open file description (and offset), which is
            // exactly the shared FileStream reference.
            var newFd = (int)Interlocked.Increment(ref _nextFileDescriptor);
            _openFiles[newFd] = stream;
            ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "wdUufa9g-D8", ExportName = "dup2",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixDup2(CpuContext ctx)
    {
        var oldFd = unchecked((int)ctx[CpuRegister.Rdi]);
        var newFd = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(oldFd, out var stream))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (oldFd == newFd)
            {
                ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            // If newFd names an open file, dup2 closes it first.
            if (_openFiles.TryGetValue(newFd, out var existing) && !ReferenceEquals(existing, stream))
            {
                try { existing.Dispose(); } catch (IOException) { }
            }

            _openFiles[newFd] = stream;
            ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- fcntl ----

    [SysAbiExport(Nid = "8nY19bKoiZk", ExportName = "fcntl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFcntl(CpuContext ctx) => KernelFcntlCore(ctx);

    [SysAbiExport(Nid = "SoZkxZkCHaw", ExportName = "sceKernelFcntl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFcntl(CpuContext ctx) => KernelFcntlCore(ctx);

    private static int KernelFcntlCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var command = unchecked((int)ctx[CpuRegister.Rsi]);
        var argument = unchecked((int)ctx[CpuRegister.Rdx]);

        switch (command)
        {
            case F_DUPFD:
                lock (_fdGate)
                {
                    if (!_openFiles.TryGetValue(fd, out var stream))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                    }

                    var newFd = Math.Max((int)Interlocked.Increment(ref _nextFileDescriptor), argument);
                    _openFiles[newFd] = stream;
                    ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
                }

                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            case F_GETFD:
            case F_GETFL:
                // No close-on-exec / status flags are tracked; report cleared.
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            case F_SETFD:
            case F_SETFL:
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            default:
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
    }

    // ---- poll / select ----
    // Regular files are always ready for both read and write, so report every
    // requested descriptor as immediately ready.

    [SysAbiExport(Nid = "ku7D4q1Y9PI", ExportName = "poll",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPoll(CpuContext ctx)
    {
        var fdsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (fdsAddress == 0 || count == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // struct pollfd { int fd; short events; short revents; } == 8 bytes.
        var ready = 0;
        Span<byte> buffer = stackalloc byte[8];
        for (uint i = 0; i < count && i < 4096; i++)
        {
            var entry = fdsAddress + i * 8;
            if (!ctx.Memory.TryRead(entry, buffer))
            {
                break;
            }

            var events = BinaryPrimitives.ReadInt16LittleEndian(buffer[4..]);
            var revents = (short)(events & (POLLIN | POLLOUT));
            BinaryPrimitives.WriteInt16LittleEndian(buffer[6..], revents);
            if (revents != 0)
            {
                ready++;
            }

            _ = ctx.Memory.TryWrite(entry, buffer);
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)ready);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "T8fER+tIGgk", ExportName = "select",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSelect(CpuContext ctx)
    {
        // nfds in Rdi; the fd_sets are left as-is (all reported ready) and the
        // ready count returned is nfds so callers proceed without blocking.
        var nfds = unchecked((int)ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = unchecked((ulong)Math.Max(nfds, 0));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Asynchronous I/O (executed synchronously) ----

    [SysAbiExport(Nid = "HgX7+AORI58", ExportName = "sceKernelAioSubmitReadCommands",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitReadCommands(CpuContext ctx) => KernelAioSubmit(ctx, write: false);

    [SysAbiExport(Nid = "lXT0m3P-vs4", ExportName = "sceKernelAioSubmitReadCommandsMultiple",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitReadCommandsMultiple(CpuContext ctx) => KernelAioSubmit(ctx, write: false);

    [SysAbiExport(Nid = "XQ8C8y+de+E", ExportName = "sceKernelAioSubmitWriteCommands",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitWriteCommands(CpuContext ctx) => KernelAioSubmit(ctx, write: true);

    private static int KernelAioSubmit(CpuContext ctx, bool write)
    {
        var requestsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((int)ctx[CpuRegister.Rsi]);
        var outIdAddress = ctx[CpuRegister.Rcx];
        if (requestsAddress == 0 || count <= 0 || count > 0x10000)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // Each SceKernelAioRWRequest: offset(8) nbyte(8) buf(8) result(8) fd(4).
        Span<byte> request = stackalloc byte[SizeofAioRwRequest];
        Span<byte> result = stackalloc byte[16];
        for (var i = 0; i < count; i++)
        {
            var entry = requestsAddress + (ulong)(i * SizeofAioRwRequest);
            if (!ctx.Memory.TryRead(entry, request))
            {
                break;
            }

            var offset = BinaryPrimitives.ReadInt64LittleEndian(request[0..]);
            var nbyte = (int)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(request[8..]), int.MaxValue);
            var buf = BinaryPrimitives.ReadUInt64LittleEndian(request[16..]);
            var resultPtr = BinaryPrimitives.ReadUInt64LittleEndian(request[24..]);
            var fd = BinaryPrimitives.ReadInt32LittleEndian(request[32..]);

            long transferred = KernelAioTransfer(ctx, fd, offset, nbyte, buf, write);
            if (resultPtr != 0)
            {
                BinaryPrimitives.WriteInt64LittleEndian(result[0..], transferred);
                BinaryPrimitives.WriteUInt32LittleEndian(result[8..], AioStateCompleted);
                _ = ctx.Memory.TryWrite(resultPtr, result);
            }
        }

        var submitId = unchecked((uint)Interlocked.Increment(ref _nextAioSubmitId));
        _aioResults[submitId] = (int)AioStateCompleted;
        if (outIdAddress != 0)
        {
            Span<byte> idBuffer = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(idBuffer, submitId);
            _ = ctx.Memory.TryWrite(outIdAddress, idBuffer);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static long KernelAioTransfer(CpuContext ctx, int fd, long offset, int nbyte, ulong buf, bool write)
    {
        if (nbyte <= 0 || buf == 0 || offset < 0)
        {
            return 0;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return -1;
        }

        var scratch = GC.AllocateUninitializedArray<byte>(nbyte);
        try
        {
            if (write)
            {
                if (!ctx.Memory.TryRead(buf, scratch))
                {
                    return -1;
                }

                WriteOpenFileAt(stream, scratch, offset);
                return nbyte;
            }

            var read = ReadOpenFileAt(stream, scratch, offset);
            if (read > 0 && !ctx.Memory.TryWrite(buf, scratch.AsSpan(0, read)))
            {
                return -1;
            }

            return read;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    [SysAbiExport(Nid = "o7O4z3jwKzo", ExportName = "sceKernelAioPollRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioPollRequests(CpuContext ctx) => KernelAioComplete(ctx);

    [SysAbiExport(Nid = "2pOuoWoCxdk", ExportName = "sceKernelAioPollRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioPollRequest(CpuContext ctx) => KernelAioGetRequestState(ctx);

    [SysAbiExport(Nid = "lgK+oIWkJyA", ExportName = "sceKernelAioWaitRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioWaitRequests(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rcx] is not (1 or 2))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        return KernelAioComplete(ctx);
    }

    [SysAbiExport(Nid = "KOF-oJbQVvc", ExportName = "sceKernelAioWaitRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioWaitRequest(CpuContext ctx) => KernelAioGetRequestState(ctx);

    private static int KernelAioGetRequestState(CpuContext ctx)
    {
        var submitId = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!_aioResults.TryGetValue(submitId, out var completedState))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // Submission finishes the transfer before it publishes the request ID.
        Span<byte> state = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(state, completedState);
        if (!ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int KernelAioComplete(CpuContext ctx)
    {
        var requestIdsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((int)ctx[CpuRegister.Rsi]);
        var statesAddress = ctx[CpuRegister.Rdx];
        if (count <= 0 || count > 128)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var byteCount = (ulong)count * sizeof(uint);
        if (requestIdsAddress == 0 || statesAddress == 0 ||
            requestIdsAddress > ulong.MaxValue - byteCount || statesAddress > ulong.MaxValue - byteCount)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        Span<byte> state = stackalloc byte[sizeof(uint)];
        for (var index = 0; index < count; index++)
        {
            if (!ctx.Memory.TryRead(requestIdsAddress + (ulong)index * sizeof(uint), state))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            var submitId = BinaryPrimitives.ReadUInt32LittleEndian(state);
            if (!_aioResults.TryGetValue(submitId, out var completedState))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            BinaryPrimitives.WriteInt32LittleEndian(state, completedState);
            if (!ctx.Memory.TryWrite(statesAddress + (ulong)index * sizeof(uint), state))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "fR521KIGgb8", ExportName = "sceKernelAioCancelRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioCancelRequest(CpuContext ctx)
    {
        // Already completed; report the completed state if an out pointer given.
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress != 0)
        {
            Span<byte> state = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(state, AioStateCompleted);
            _ = ctx.Memory.TryWrite(stateAddress, state);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "5TgME6AYty4", ExportName = "sceKernelAioDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioDeleteRequest(CpuContext ctx)
    {
        var submitId = unchecked((uint)ctx[CpuRegister.Rdi]);
        _aioResults.TryRemove(submitId, out _);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
