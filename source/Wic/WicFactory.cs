/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using Vortice.Win32;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// Creates the per-call <see cref="IWICImagingFactory"/>.
/// <para>
/// A factory is never cached across calls. WIC objects belong to the apartment that created
/// them, and the host calls into the plugin from arbitrary background threads, so the only
/// safe lifetime is "inside one ABI call" (or, for encoding, one single-threaded session).
/// <c>CoCreateInstance</c> of an in-proc class is cheap enough that this costs nothing
/// measurable next to decoding an image.
/// </para>
/// </summary>
internal static unsafe class WicFactory
{
    /// <summary>
    /// Creates an imaging factory on the calling thread, or returns <c>null</c> when COM is
    /// unavailable. The caller owns the reference.
    /// </summary>
    public static IWICImagingFactory* Create()
    {
        if (!ComInterop.EnsureApartment()) return null;

        var clsid = WIC.CLSID_WICImagingFactory;
        var iid = IWICImagingFactory.IID_IWICImagingFactory;
        IWICImagingFactory* factory = null;

        var hr = Vortice.Win32.Apis.CoCreateInstance(&clsid, null,
            (uint)Vortice.Win32.Apis.CLSCTX_INPROC_SERVER, &iid, (void**)&factory);

        return hr.Failure ? null : factory;
    }
}
