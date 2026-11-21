/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using Vortice.Win32.Com;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// Typed reads through a WIC metadata query reader.
/// </summary>
internal static unsafe class WicMetadata
{
    /// <summary>Reads an unsigned integer or boolean value; <c>false</c> when absent or of another type.</summary>
    public static bool TryGetUInt(IWICMetadataQueryReader* reader, string query, out uint value)
    {
        value = 0;
        if (reader == null) return false;

        ComInterop.PropVariant v = default;
        if (!TryRead(reader, query, &v)) return false;

        try
        {
            switch (v.Vt)
            {
                case ComInterop.VT_UI1: value = (byte)v.Value0; return true;
                case ComInterop.VT_UI2: value = (ushort)v.Value0; return true;
                case ComInterop.VT_UI4: value = (uint)v.Value0; return true;
                case ComInterop.VT_BOOL: value = (ushort)v.Value0 != 0 ? 1u : 0u; return true;
                default: return false;
            }
        }
        finally
        {
            ComInterop.PropVariantClear(&v);
        }
    }


    /// <summary>Reads a <c>VT_UI1 | VT_VECTOR</c> value, or <c>null</c> when absent.</summary>
    public static byte[]? GetBytes(IWICMetadataQueryReader* reader, string query)
    {
        if (reader == null) return null;

        ComInterop.PropVariant v = default;
        if (!TryRead(reader, query, &v)) return null;

        try
        {
            if (v.Vt != (ComInterop.VT_UI1 | ComInterop.VT_VECTOR)) return null;

            // CAUB: a 32-bit count, then the element pointer in the second union slot.
            var count = (int)(uint)v.Value0;
            var data = *(byte**)&v.Value1;
            if (count <= 0 || data == null) return [];

            return new ReadOnlySpan<byte>(data, count).ToArray();
        }
        finally
        {
            ComInterop.PropVariantClear(&v);
        }
    }


    private static bool TryRead(IWICMetadataQueryReader* reader, string query, ComInterop.PropVariant* value)
    {
        fixed (char* pQuery = query)
        {
            return reader->GetMetadataByName(pQuery, (Variant**)value).Success;
        }
    }
}
