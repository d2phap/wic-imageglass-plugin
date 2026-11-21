/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using ImageGlass.SDK.Plugins;
using Vortice.Win32;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// The pixel format the plugin will hand the host for one source image, and what that
/// implies about dynamic range and alpha.
/// </summary>
internal readonly record struct PixelPlan(Guid TargetFormat, IGPixelFormat IgFormat,
    int BytesPerPixel, IGHdrTransferFn Hdr, bool HasAlpha);


/// <summary>
/// Maps a WIC source pixel format onto one of the four formats the host understands
/// (<see cref="IGPixelFormat"/>), preserving as much precision as the host can consume.
/// </summary>
internal static unsafe class WicPixels
{
    /// <summary>
    /// Whether the host understands <see cref="IGHdrTransferFn.ScRgb"/>; set once from the ABI
    /// version handed to <c>ig_plugin_get_api</c>, before any codec call can run.
    /// </summary>
    public static bool HostSupportsScRgb { get; set; }


    /// <summary>
    /// Picks the host-facing format for <paramref name="sourceFormat"/>.
    /// <para>
    /// The classification is driven by WIC's own numeric-representation metadata rather than a
    /// GUID whitelist, so a pixel format introduced by a third-party codec is classified
    /// correctly too. WIC infers scRGB for every float and fixed-point format, so those are
    /// <see cref="IGHdrTransferFn.ScRgb"/>, NOT the scene-referred <see cref="IGHdrTransferFn.Linear"/>.
    /// </para>
    /// </summary>
    public static PixelPlan Choose(IWICImagingFactory* factory, Guid sourceFormat)
    {
        // HDR10 is the one format whose transfer function is not implied by its numeric
        // representation: 10-bit unsigned integers carrying PQ-encoded values.
        if (sourceFormat == WIC.GUID_WICPixelFormat32bppR10G10B10A2HDR10)
        {
            return new PixelPlan(WIC.GUID_WICPixelFormat64bppRGBAHalf,
                IGPixelFormat.RgbaFloat16, 8, IGHdrTransferFn.PQ, true);
        }

        var bpp = 32u;
        var representation = WICPixelFormatNumericRepresentation.UnsignedInteger;
        var hasAlpha = false;
        ReadFormatTraits(factory, sourceFormat, ref bpp, ref representation, ref hasAlpha);

        var isExtendedRange = representation is WICPixelFormatNumericRepresentation.Float
            or WICPixelFormatNumericRepresentation.Fixed
            or WICPixelFormatNumericRepresentation.SignedInteger;

        if (isExtendedRange)
        {
            // Linear is the pre-1.1 spelling: wrong by 203/80, but an old host at least tone-maps.
            var extendedFn = HostSupportsScRgb ? IGHdrTransferFn.ScRgb : IGHdrTransferFn.Linear;

            return new PixelPlan(WIC.GUID_WICPixelFormat64bppRGBAHalf,
                IGPixelFormat.RgbaFloat16, 8, extendedFn, hasAlpha);
        }

        // Deep but ordinary integer formats (16 bit per channel TIFF, PNG, JXR, camera raw)
        // keep their precision; everything else lands on plain BGRA8.
        if (bpp > 32 && representation != WICPixelFormatNumericRepresentation.Indexed)
        {
            return new PixelPlan(WIC.GUID_WICPixelFormat64bppRGBA,
                IGPixelFormat.Rgba16Unorm, 8, IGHdrTransferFn.None, hasAlpha);
        }

        return new PixelPlan(WIC.GUID_WICPixelFormat32bppBGRA,
            IGPixelFormat.Bgra8Unorm, 4, IGHdrTransferFn.None, hasAlpha);
    }


    /// <summary>
    /// The formats to try, in order, when <see cref="PixelPlan.TargetFormat"/> has no
    /// installed converter. Every WIC install can produce 32bppBGRA, so the chain terminates.
    /// </summary>
    public static PixelPlan Degrade(in PixelPlan plan)
    {
        if (plan.IgFormat == IGPixelFormat.RgbaFloat16)
        {
            return plan with
            {
                TargetFormat = WIC.GUID_WICPixelFormat64bppRGBA,
                IgFormat = IGPixelFormat.Rgba16Unorm,
                BytesPerPixel = 8,
                // Half-float was refused, so the extended range cannot survive the round trip;
                // claiming HDR here would make the host tone-map already-clamped pixels.
                Hdr = IGHdrTransferFn.None,
            };
        }

        return plan with
        {
            TargetFormat = WIC.GUID_WICPixelFormat32bppBGRA,
            IgFormat = IGPixelFormat.Bgra8Unorm,
            BytesPerPixel = 4,
            Hdr = IGHdrTransferFn.None,
        };
    }


    /// <summary>
    /// Reads bit depth, numeric representation and transparency for a pixel format GUID.
    /// Leaves the defaults in place when the component info is unavailable.
    /// </summary>
    private static void ReadFormatTraits(IWICImagingFactory* factory, Guid format,
        ref uint bpp, ref WICPixelFormatNumericRepresentation representation, ref bool hasAlpha)
    {
        IWICComponentInfo* component = null;
        IWICPixelFormatInfo2* info = null;

        try
        {
            if (factory->CreateComponentInfo(&format, &component).Failure) return;

            var iid = IWICPixelFormatInfo2.IID_IWICPixelFormatInfo2;
            if (component->QueryInterface(&iid, (void**)&info).Failure) return;

            uint bits;
            if (info->GetBitsPerPixel(&bits).Success) bpp = bits;

            WICPixelFormatNumericRepresentation rep;
            if (info->GetNumericRepresentation(&rep).Success) representation = rep;

            Bool32 transparency;
            if (info->SupportsTransparency(&transparency).Success) hasAlpha = transparency.Value != 0;
        }
        finally
        {
            ComInterop.Release(ref info);
            ComInterop.Release(ref component);
        }
    }
}
