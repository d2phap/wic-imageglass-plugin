/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using Vortice.Win32;
using Vortice.Win32.Com;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// Camera raw quirks of the two Microsoft raw decoders.
/// </summary>
internal static unsafe class WicRaw
{
    /// <summary>
    /// The legacy built-in DNG decoder. WIC picks it first for .dng, yet for most DNGs its only frame is the embedded preview.
    /// </summary>
    private static readonly Guid LegacyDngDecoder = new("981d9411-909e-42a7-8f5d-a747ff052edb");

    /// <summary>
    /// The Raw Image Extension decoder, which decodes the sensor data of DNG and the camera formats.
    /// </summary>
    private static readonly Guid RawImageDecoder = new("41945702-8302-44a6-9445-ac98e8afa086");


    /// <summary>
    /// Replaces the legacy DNG decoder with the Raw Image decoder when that one opens the file with a larger frame.
    /// </summary>
    public static void PreferRawImageDecoder(IWICImagingFactory* factory, string path, ref IWICBitmapDecoder* decoder)
    {
        if (DecoderClsid(decoder) != LegacyDngDecoder) return;

        // null when the extension is not installed, or for the old lossy-JPEG DNGs it refuses
        var candidate = OpenWith(factory, path, RawImageDecoder);
        if (candidate == null) return;

        if (FrameArea(candidate) > FrameArea(decoder))
        {
            ComInterop.Release(ref decoder);
            decoder = candidate;
        }
        else
        {
            ComInterop.Release(ref candidate);
        }
    }


    private static IWICBitmapDecoder* OpenWith(IWICImagingFactory* factory, string path, Guid clsid)
    {
        IWICBitmapDecoder* decoder = null;
        IWICStream* stream = null;

        try
        {
            var iid = IWICBitmapDecoder.IID_IWICBitmapDecoder;
            if (Vortice.Win32.Apis.CoCreateInstance(&clsid, null,
                (uint)Vortice.Win32.Apis.CLSCTX_INPROC_SERVER, &iid, (void**)&decoder).Failure) return null;

            if (factory->CreateStream(&stream).Failure) return null;

            HRESULT hr;
            fixed (char* pPath = path)
            {
                hr = stream->InitializeFromFilename(pPath, ComInterop.GENERIC_READ);
            }
            if (hr.Failure) return null;

            // The decoder keeps its own reference to the stream.
            if (decoder->Initialize((IStream*)stream, WIC.WICDecodeMetadataCacheOnDemand).Failure) return null;

            var opened = decoder;
            decoder = null;
            return opened;
        }
        finally
        {
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref stream);
        }
    }


    private static Guid DecoderClsid(IWICBitmapDecoder* decoder)
    {
        IWICBitmapDecoderInfo* info = null;
        try
        {
            if (decoder->GetDecoderInfo(&info).Failure || info == null) return default;

            Guid clsid;
            return info->GetCLSID(&clsid).Success ? clsid : default;
        }
        finally
        {
            ComInterop.Release(ref info);
        }
    }


    private static ulong FrameArea(IWICBitmapDecoder* decoder)
    {
        IWICBitmapFrameDecode* frame = null;
        try
        {
            if (decoder->GetFrame(0, &frame).Failure) return 0;

            uint width = 0, height = 0;
            return ((IWICBitmapSource*)frame)->GetSize(&width, &height).Success ? (ulong)width * height : 0;
        }
        finally
        {
            ComInterop.Release(ref frame);
        }
    }
}
