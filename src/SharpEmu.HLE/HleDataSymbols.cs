// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.HLE;

public static class HleDataSymbols
{
    private const string StackChkGuardNid = "f7uOxY9mM1U";
    private const string ProgNameNid = "djxxOmW6-aw";
    private const string LibcNeedFlagNid = "P330P3dFF68";
    private const string LibcInternalNeedFlagNid = "ZT4ODD2Ts9o";
    private const string EnvironNid = "+2thxYZ4syk";
    private const string StdinPointerNid = "bgAcsbcEznc";
    private const string StdoutPointerNid = "zqJhBxAKfsc";
    private const string StderrPointerNid = "as8Od-tH1BI";
    private const string IsThreadedNid = "q1OvUam0BJU";
    private const int ProgNameMaxBytes = 511;
    // Terminator canaries reserve the low byte as NUL. Keep the process data
    // symbol and every per-thread TLS copy byte-for-byte identical.
    private const ulong StackChkGuardValue = 0xC0DEC0DECAFEBA00UL;

    private static readonly object _gate = new();
    private static readonly nint _stackChkGuardAddress = Allocate(sizeof(ulong) * 2);
    private static readonly nint _progNameBufferAddress = Allocate(ProgNameMaxBytes + 1);
    private static readonly nint _progNamePointerAddress = Allocate(nint.Size);
    private static readonly nint _argumentVectorAddress = Allocate(nint.Size * 2);
    private static readonly nint _libcNeedFlagAddress = Allocate(sizeof(uint));
    private static readonly nint _libcInternalNeedFlagAddress = Allocate(sizeof(uint));
    private static readonly nint _environVectorAddress = Allocate(nint.Size * 5);
    private static readonly nint _environVariableAddress = Allocate(nint.Size);
    private static readonly nint _stdinPointerAddress = Allocate(nint.Size);
    private static readonly nint _stdoutPointerAddress = Allocate(nint.Size);
    private static readonly nint _stderrPointerAddress = Allocate(nint.Size);
    private static readonly nint _isThreadedAddress = Allocate(sizeof(int));
    private static int _processArgumentCount;
    private static ulong _processArgumentVector;

    static HleDataSymbols()
    {
        if (_stackChkGuardAddress != 0)
        {
            Marshal.WriteInt64(_stackChkGuardAddress, unchecked((long)StackChkGuardValue));
            Marshal.WriteInt64(
                IntPtr.Add(_stackChkGuardAddress, sizeof(ulong)),
                unchecked((long)StackChkGuardValue));
        }

        if (_libcNeedFlagAddress != 0)
        {
            Marshal.WriteInt32(_libcNeedFlagAddress, 1);
        }

        if (_libcInternalNeedFlagAddress != 0)
        {
            Marshal.WriteInt32(_libcInternalNeedFlagAddress, 1);
        }

        ConfigureEnviron();
        ConfigureStdio();
        ConfigureProcessImageName("eboot.bin");
    }

    public static IEnumerable<string> EnumerateKnownNids()
    {
        yield return StackChkGuardNid;
        yield return ProgNameNid;
        yield return LibcNeedFlagNid;
        yield return LibcInternalNeedFlagNid;
        yield return EnvironNid;
        yield return StdinPointerNid;
        yield return StdoutPointerNid;
        yield return StderrPointerNid;
        yield return IsThreadedNid;
        // Libraries that were not passed through the NID rewriter still import
        // these data symbols by their plain FreeBSD names.
        yield return "__stack_chk_guard";
        yield return "__stderrp";
        // libSDL3 and other FreeBSD-ABI natives import environ with an ELF
        // version suffix. SelfLoader strips the suffix before data rebinding.
        yield return "environ";
        yield return "__environ";
    }

    public static int ProcessArgumentCount =>
        Volatile.Read(ref _processArgumentVector) != 0 ? Volatile.Read(ref _processArgumentCount) :
        _progNameBufferAddress != 0 && _argumentVectorAddress != 0 ? 1 : 0;

    public static ulong ProcessArgumentVectorAddress
    {
        get
        {
            var address = Volatile.Read(ref _processArgumentVector);
            return address != 0 ? address : unchecked((ulong)_argumentVectorAddress);
        }
    }

    public static void ConfigureProcessArguments(int count, ulong vector)
    {
        Volatile.Write(ref _processArgumentCount, count);
        Volatile.Write(ref _processArgumentVector, vector);
    }

    public static void ConfigureProcessImageName(string? processImageName)
    {
        var effectiveName = string.IsNullOrWhiteSpace(processImageName)
            ? "eboot.bin"
            : processImageName;
        var encodedName = Encoding.UTF8.GetBytes(effectiveName);
        var byteCount = Math.Min(encodedName.Length, ProgNameMaxBytes);

        lock (_gate)
        {
            ConfigureProcessArguments(0, 0);
            if (_progNameBufferAddress == 0 ||
                _progNamePointerAddress == 0 ||
                _argumentVectorAddress == 0)
            {
                return;
            }

            for (var i = 0; i <= ProgNameMaxBytes; i++)
            {
                Marshal.WriteByte(_progNameBufferAddress, i, 0);
            }

            Marshal.Copy(encodedName, 0, _progNameBufferAddress, byteCount);
            WritePointer(_progNamePointerAddress, _progNameBufferAddress);
            WritePointer(_argumentVectorAddress, _progNameBufferAddress);
            WritePointer(IntPtr.Add(_argumentVectorAddress, nint.Size), 0);
        }
    }

    private static void ConfigureEnviron()
    {
        if (_environVectorAddress == 0 || _environVariableAddress == 0)
        {
            return;
        }

        string[] values =
        [
            "PS5CRAFT=1",
            "HOME=/app0",
            "TMPDIR=/tmp",
            "PATH=/app0/assets/java25/bin",
        ];

        for (var i = 0; i < values.Length; i++)
        {
            var valueAddress = Allocate(values[i].Length + 1);
            if (valueAddress == 0)
            {
                continue;
            }

            var encoded = Encoding.UTF8.GetBytes(values[i]);
            Marshal.Copy(encoded, 0, valueAddress, encoded.Length);
            Marshal.WriteByte(valueAddress, encoded.Length, 0);
            WritePointer(IntPtr.Add(_environVectorAddress, i * nint.Size), valueAddress);
        }

        WritePointer(IntPtr.Add(_environVectorAddress, values.Length * nint.Size), 0);
        WritePointer(_environVariableAddress, _environVectorAddress);
    }

    private static void ConfigureStdio()
    {
        if (_stdinPointerAddress == 0 || _stdoutPointerAddress == 0 ||
            _stderrPointerAddress == 0 || _isThreadedAddress == 0)
        {
            return;
        }

        // HLE stdio treats unknown stream pointers as the host console. Give
        // each standard FILE symbol a non-zero opaque object so caller-side
        // NULL checks and FILE* comparisons behave like a normal libc.
        WritePointer(_stdinPointerAddress, Allocate(64));
        WritePointer(_stdoutPointerAddress, Allocate(64));
        WritePointer(_stderrPointerAddress, Allocate(64));
        Marshal.WriteInt32(_isThreadedAddress, 1);
    }

    public static bool TryGetAddress(string nid, out ulong address)
    {
        var pointer = nid switch
        {
            StackChkGuardNid => _stackChkGuardAddress,
            ProgNameNid => _progNamePointerAddress,
            LibcNeedFlagNid => _libcNeedFlagAddress,
            LibcInternalNeedFlagNid => _libcInternalNeedFlagAddress,
            EnvironNid => _environVariableAddress,
            StdinPointerNid => _stdinPointerAddress,
            StdoutPointerNid => _stdoutPointerAddress,
            StderrPointerNid => _stderrPointerAddress,
            IsThreadedNid => _isThreadedAddress,
            "__stack_chk_guard" => _stackChkGuardAddress,
            "__stderrp" => _stderrPointerAddress,
            "environ" => _environVariableAddress,
            "__environ" => _environVariableAddress,
            _ => 0,
        };

        if (pointer == 0)
        {
            address = 0;
            return false;
        }

        address = unchecked((ulong)pointer);
        return true;
    }

    private static nint Allocate(int size)
    {
        try
        {
            var memory = Marshal.AllocHGlobal(size);
            for (var i = 0; i < size; i++)
            {
                Marshal.WriteByte(memory, i, 0);
            }

            return memory;
        }
        catch
        {
            return 0;
        }
    }

    private static void WritePointer(nint target, nint value)
    {
        if (nint.Size == sizeof(int))
        {
            Marshal.WriteInt32(target, value.ToInt32());
            return;
        }

        Marshal.WriteInt64(target, value.ToInt64());
    }
}
