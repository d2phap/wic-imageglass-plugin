/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Win32;

namespace WicCodec.Wic;

/// <summary>
/// The COM plumbing Vortice does not cover: apartment setup, and the two OLE PODs
/// (<c>PROPBAG2</c> / <c>PROPVARIANT</c>) whose value fields Vortice keeps private.
/// </summary>
internal static unsafe partial class ComInterop
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    public const int WINCODEC_ERR_COMPONENTNOTFOUND = unchecked((int)0x88982F50);
    public const int WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT = unchecked((int)0x88982F80);
    public const int WINCODEC_ERR_UNSUPPORTEDOPERATION = unchecked((int)0x88982F81);
    public const int WINCODEC_ERR_PROPERTYNOTSUPPORTED = unchecked((int)0x88982F41);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    public const uint GENERIC_WRITE = 0x40000000;
    private const uint COINIT_MULTITHREADED = 0x0;
    private const uint COINIT_DISABLE_OLE1DDE = 0x4;

    [ThreadStatic] private static bool _comReady;


    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(void* pvReserved, uint dwCoInit);


    /// <summary>
    /// Frees whatever a <c>PROPVARIANT</c> owns. A no-op for the scalar types, but metadata
    /// reads can hand back <c>VT_LPWSTR</c> / vector values that would otherwise leak.
    /// </summary>
    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(PropVariant* pvar);


    /// <summary>
    /// Joins this thread to an apartment so WIC objects can be created on it.
    /// <para>
    /// Deliberately never paired with <c>CoUninitialize</c>. Multi-frame encode sessions span
    /// several ABI calls on one thread, so tearing the apartment down between them would orphan
    /// the encoder; and the host's decode threads are per-task <c>LongRunning</c> threads whose
    /// apartment Windows reclaims at thread exit anyway. <c>RPC_E_CHANGED_MODE</c> means the
    /// thread is already an STA (e.g. the UI thread) — WIC works there too, and every object we
    /// create stays on the thread that created it.
    /// </para>
    /// </summary>
    public static bool EnsureApartment()
    {
        if (_comReady) return true;

        var hr = CoInitializeEx(null, COINIT_MULTITHREADED | COINIT_DISABLE_OLE1DDE);
        _comReady = hr >= 0 || hr == RPC_E_CHANGED_MODE;
        return _comReady;
    }


    /// <summary>Releases a COM pointer and nulls it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Release<T>(ref T* obj) where T : unmanaged
    {
        if (obj == null) return;

        // Every Vortice COM struct starts with lpVtbl, so any of them is an IUnknown.
        ((IUnknown*)obj)->Release();
        obj = null;
    }


    /// <summary>
    /// <c>PROPBAG2</c>. Vortice's <c>PropertyBagMetadata</c> exposes its variant type as a
    /// read-only property, so writing an encoder option needs the raw layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropBag2
    {
        public uint Type;
        public ushort Vt;
        public ushort CfType;
        public uint Hint;
        public char* Name;
        public Guid ClassId;
    }


    /// <summary>
    /// <c>PROPVARIANT</c>: 8 bytes of header then a 16-byte union, so it also covers the
    /// counted-array forms (<c>CAUB</c>) the GIF loop extension needs. Layout-compatible with
    /// <c>VARIANT</c>, which is all <c>IPropertyBag2::Write</c> reads.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nuint Value0;
        public nuint Value1;

        public static PropVariant FromBool(bool value) => new()
        {
            Vt = VT_BOOL,
            // VARIANT_TRUE is -1, not 1.
            Value0 = (nuint)(ushort)(value ? 0xFFFF : 0x0000),
        };

        public static PropVariant FromFloat(float value)
        {
            var v = new PropVariant { Vt = VT_R4 };
            *(float*)&v.Value0 = value;
            return v;
        }

        public static PropVariant FromByte(byte value) => new() { Vt = VT_UI1, Value0 = value };

        public static PropVariant FromUInt16(ushort value) => new() { Vt = VT_UI2, Value0 = value };

        /// <summary>
        /// A <c>VT_UI1 | VT_VECTOR</c> pointing at caller-owned bytes that must outlive the call.
        /// </summary>
        public static PropVariant FromByteVector(byte* data, int count)
        {
            var v = new PropVariant { Vt = VT_UI1 | VT_VECTOR, Value0 = (nuint)(uint)count };
            *(byte**)&v.Value1 = data;
            return v;
        }
    }

    public const ushort VT_UI1 = 17;
    public const ushort VT_UI2 = 18;
    public const ushort VT_UI4 = 19;
    public const ushort VT_R4 = 4;
    public const ushort VT_BOOL = 11;
    public const ushort VT_VECTOR = 0x1000;
}
