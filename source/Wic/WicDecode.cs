/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using ImageGlass.SDK.Plugins;
using Vortice.Win32;
using Vortice.Win32.Com;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// Reads images through WIC: metadata probing and single-frame raster decoding.
/// <para>
/// Every entry point opens the file, does its work and releases every COM object before
/// returning. Nothing is cached between calls, because a WIC object belongs to the apartment
/// that created it and the host calls in from arbitrary background threads.
/// </para>
/// </summary>
internal static unsafe class WicDecode
{
    /// <summary>
    /// EXIF orientation is baked into the pixels by <see cref="Decode"/>, so the tags below are
    /// only read to know which rotation to apply and how the reported size must be swapped.
    /// Containers put the IFD in different places; the first path that resolves wins.
    /// </summary>
    private static readonly string[] _orientationQueries =
        ["/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "/app1/{ushort=274}", "/{ushort=274}"];


    /// <summary>
    /// Fills <paramref name="outInfo"/> from the file's first frame.
    /// </summary>
    public static IGStatus LoadMetadata(string path, IGImageInfo* outInfo, void* cancellation)
    {
        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;

        try
        {
            var status = Open(path, cancellation, ref factory, ref decoder);
            if (status != IGStatus.OK) return status;

            uint frameCount = 0;
            decoder->GetFrameCount(&frameCount);
            if (frameCount == 0) frameCount = 1;

            if (decoder->GetFrame(0, &frame).Failure) return IGStatus.DecodeFailed;

            uint width = 0, height = 0;
            if (frame->GetSize(&width, &height).Failure || width == 0 || height == 0)
            {
                return IGStatus.DecodeFailed;
            }

            Guid sourceFormat;
            if (frame->GetPixelFormat(&sourceFormat).Failure) sourceFormat = default;
            var plan = WicPixels.Choose(factory, sourceFormat);

            var orientation = ReadOrientation(frame);
            if (SwapsAxes(orientation)) (width, height) = (height, width);

            outInfo->Width = (int)width;
            outInfo->Height = (int)height;
            outInfo->PixelFormat = (int)plan.IgFormat;
            outInfo->HasAlpha = plan.HasAlpha ? 1 : 0;
            outInfo->HdrTransferFn = (int)plan.Hdr;
            outInfo->FrameCount = (int)frameCount;
            outInfo->FileSizeBytes = FileSizeOf(path);

            // The decoded buffer is already upright, so reporting the source tag would make a
            // host that honors it rotate a second time.
            outInfo->Orientation = 1;

            ApplyColorProfile(factory, frame, outInfo);
            return IGStatus.OK;
        }
        finally
        {
            ComInterop.Release(ref frame);
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref factory);
        }
    }


    /// <summary>
    /// Decodes one frame at full size into a freshly allocated native buffer owned by the host.
    /// </summary>
    public static IGStatus Decode(string path, int frameIndex, IGPixelBuffer* outBuf, void* cancellation)
        => DecodeCore(path, frameIndex, 0, 0, outBuf, cancellation);


    /// <summary>
    /// Decodes one frame no larger than <paramref name="maxWidth"/> x <paramref name="maxHeight"/>,
    /// for the host's thumbnails and previews.
    /// </summary>
    public static IGStatus DecodeScaled(string path, int frameIndex, int maxWidth, int maxHeight,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return IGStatus.InvalidArg;

        return DecodeCore(path, frameIndex, maxWidth, maxHeight, outBuf, cancellation);
    }


    /// <summary>
    /// The one decode path. A non-zero box scales the frame down to fit it; 0 means full size.
    /// </summary>
    private static IGStatus DecodeCore(string path, int frameIndex, int maxWidth, int maxHeight,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapScaler* scaler = null;
        IWICBitmapSource* converted = null;
        IWICBitmapFlipRotator* rotator = null;
        byte* pixels = null;

        try
        {
            var status = Open(path, cancellation, ref factory, ref decoder);
            if (status != IGStatus.OK) return status;

            uint frameCount = 0;
            decoder->GetFrameCount(&frameCount);
            if (frameIndex < 0 || (frameCount > 0 && frameIndex >= frameCount)) return IGStatus.InvalidArg;

            if (decoder->GetFrame((uint)frameIndex, &frame).Failure) return IGStatus.DecodeFailed;

            Guid sourceFormat;
            if (frame->GetPixelFormat(&sourceFormat).Failure) sourceFormat = default;
            var plan = WicPixels.Choose(factory, sourceFormat);

            // Scale, convert, then rotate. The scaler must sit on the FRAME: it reaches the
            // frame's IWICBitmapSourceTransform there (a JPEG's DCT scales), which the format
            // converter does not forward. The rotator is format-agnostic, and rotating the
            // smaller pre-conversion buffer would still need a second pass afterwards.
            var source = (IWICBitmapSource*)frame;
            if (maxWidth > 0 && maxHeight > 0)
            {
                var scaled = TryScale(factory, frame, ReadOrientation(frame),
                    (uint)maxWidth, (uint)maxHeight, &scaler);
                if (scaled != null) source = scaled;
            }

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            if (sourceFormat != plan.TargetFormat)
            {
                if (!TryConvert(source, ref plan, &converted)) return IGStatus.Unsupported;
                source = converted;
            }

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            var transform = ToTransform(ReadOrientation(frame));
            if (transform != WICBitmapTransformOptions.Rotate0)
            {
                if (factory->CreateBitmapFlipRotator(&rotator).Failure) return IGStatus.Internal;
                if (rotator->Initialize(source, transform).Failure) return IGStatus.Internal;
                source = (IWICBitmapSource*)rotator;
            }

            uint width = 0, height = 0;
            if (source->GetSize(&width, &height).Failure || width == 0 || height == 0)
            {
                return IGStatus.DecodeFailed;
            }

            var stride = (long)width * plan.BytesPerPixel;
            var byteCount = stride * height;
            if (stride > int.MaxValue || byteCount > int.MaxValue) return IGStatus.OutOfMemory;

            pixels = NativeBuffers.AllocPixels((nuint)byteCount);
            if (pixels == null) return IGStatus.OutOfMemory;

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            if (source->CopyPixels(null, (uint)stride, (uint)byteCount, pixels).Failure)
            {
                return IGStatus.DecodeFailed;
            }

            outBuf->Data = pixels;
            outBuf->Width = (int)width;
            outBuf->Height = (int)height;
            outBuf->Stride = (int)stride;
            outBuf->PixelFormat = (int)plan.IgFormat;
            outBuf->ReleaseContext = pixels;
            pixels = null;   // handed over; the host frees it via FreePixelBuffer

            return IGStatus.OK;
        }
        finally
        {
            // Only reached with a non-null pixels on a failure path.
            NativeBuffers.Discard(pixels);

            ComInterop.Release(ref rotator);
            ComInterop.Release(ref converted);
            ComInterop.Release(ref scaler);
            ComInterop.Release(ref frame);
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref factory);
        }
    }


    /// <summary>
    /// Creates a factory and opens <paramref name="path"/> with whichever installed decoder
    /// claims the content.
    /// </summary>
    private static IGStatus Open(string path, void* cancellation,
        ref IWICImagingFactory* factory, ref IWICBitmapDecoder* decoder)
    {
        if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

        factory = WicFactory.Create();
        if (factory == null) return IGStatus.Internal;

        IWICBitmapDecoder* opened = null;
        HRESULT hr;
        fixed (char* pPath = path)
        {
            hr = factory->CreateDecoderFromFilename(pPath, null, NativeFileAccess.GenericRead,
                WIC.WICDecodeMetadataCacheOnDemand, &opened);
        }

        if (hr.Failure)
        {
            // No installed codec recognized the bytes; let the host try its built-ins.
            return hr.Value == ComInterop.WINCODEC_ERR_COMPONENTNOTFOUND
                ? IGStatus.Unsupported
                : IGStatus.IoError;
        }

        decoder = opened;
        return IGStatus.OK;
    }


    /// <summary>
    /// Wraps <paramref name="frame"/> in a scaler that fits the box, or returns <c>null</c> when
    /// the frame already fits (or the scaler cannot be set up, which is not fatal).
    /// </summary>
    /// <remarks>
    /// The box applies to the UPRIGHT image, so a 90/270 degree orientation swaps the axes before
    /// the ratio is taken; the resulting scale then applies to the unrotated frame either way.
    /// </remarks>
    private static IWICBitmapSource* TryScale(IWICImagingFactory* factory,
        IWICBitmapFrameDecode* frame, int orientation, uint maxWidth, uint maxHeight,
        IWICBitmapScaler** outScaler)
    {
        uint srcWidth = 0, srcHeight = 0;
        if (((IWICBitmapSource*)frame)->GetSize(&srcWidth, &srcHeight).Failure) return null;
        if (srcWidth == 0 || srcHeight == 0) return null;

        var uprightWidth = SwapsAxes(orientation) ? srcHeight : srcWidth;
        var uprightHeight = SwapsAxes(orientation) ? srcWidth : srcHeight;

        var scale = Math.Min((double)maxWidth / uprightWidth, (double)maxHeight / uprightHeight);
        if (scale >= 1) return null;

        var targetWidth = Math.Max(1u, (uint)Math.Round(srcWidth * scale));
        var targetHeight = Math.Max(1u, (uint)Math.Round(srcHeight * scale));
        if (targetWidth >= srcWidth && targetHeight >= srcHeight) return null;

        if (factory->CreateBitmapScaler(outScaler).Failure) return null;

        // Fant is WIC's box/averaging filter: the right one for a big downscale.
        if ((*outScaler)->Initialize((IWICBitmapSource*)frame, targetWidth, targetHeight,
            WICBitmapInterpolationMode.ModeFant).Failure)
        {
            ComInterop.Release(ref *outScaler);
            return null;
        }

        return (IWICBitmapSource*)*outScaler;
    }


    /// <summary>
    /// Converts to <see cref="PixelPlan.TargetFormat"/>, stepping down the plan when this
    /// machine has no converter for the pair.
    /// </summary>
    private static bool TryConvert(IWICBitmapSource* source, ref PixelPlan plan, IWICBitmapSource** result)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var target = plan.TargetFormat;

            // WICConvertBitmapSource, not IWICImagingFactory.CreateFormatConverter: the helper
            // walks the registered converters, so it also finds the high-color and WMPhoto ones
            // that the default converter cannot stand in for.
            if (WIC.WICConvertBitmapSource(&target, source, result).Success) return true;

            var degraded = WicPixels.Degrade(in plan);
            if (degraded.TargetFormat == plan.TargetFormat) return false;
            plan = degraded;
        }

        return false;
    }


    /// <summary>
    /// Reads the EXIF orientation tag (1..8), defaulting to 1 when absent.
    /// </summary>
    private static int ReadOrientation(IWICBitmapFrameDecode* frame)
    {
        IWICMetadataQueryReader* reader = null;
        try
        {
            if (frame->GetMetadataQueryReader(&reader).Failure) return 1;

            foreach (var query in _orientationQueries)
            {
                ComInterop.PropVariant value = default;
                HRESULT hr;
                fixed (char* pQuery = query)
                {
                    hr = reader->GetMetadataByName(pQuery, (Variant**)&value);
                }
                if (hr.Failure) continue;

                var orientation = value.Vt switch
                {
                    ComInterop.VT_UI2 => (int)(ushort)value.Value0,
                    ComInterop.VT_UI1 => (int)(byte)value.Value0,
                    _ => 0,
                };
                ComInterop.PropVariantClear(&value);

                if (orientation is >= 1 and <= 8) return orientation;
            }

            return 1;
        }
        finally
        {
            ComInterop.Release(ref reader);
        }
    }


    /// <summary>
    /// Maps an EXIF orientation onto the equivalent WIC transform. WIC applies the flip after
    /// the rotation, which is why 5 and 7 pair a horizontal flip with the opposite rotation
    /// from the one their EXIF description names.
    /// </summary>
    private static WICBitmapTransformOptions ToTransform(int orientation) => orientation switch
    {
        2 => WICBitmapTransformOptions.FlipHorizontal,
        3 => WICBitmapTransformOptions.Rotate180,
        4 => WICBitmapTransformOptions.FlipVertical,
        5 => WICBitmapTransformOptions.Rotate90 | WICBitmapTransformOptions.FlipHorizontal,
        6 => WICBitmapTransformOptions.Rotate90,
        7 => WICBitmapTransformOptions.Rotate270 | WICBitmapTransformOptions.FlipHorizontal,
        8 => WICBitmapTransformOptions.Rotate270,
        _ => WICBitmapTransformOptions.Rotate0,
    };


    private static bool SwapsAxes(int orientation) => orientation is 5 or 6 or 7 or 8;


    /// <summary>
    /// Publishes the frame's embedded ICC profile, or falls back to its EXIF color-space tag.
    /// </summary>
    private static void ApplyColorProfile(IWICImagingFactory* factory, IWICBitmapFrameDecode* frame,
        IGImageInfo* outInfo)
    {
        uint count = 0;
        if (frame->GetColorContexts(0, null, &count).Failure || count == 0) return;

        // A frame can carry several contexts (e.g. a profile plus an EXIF hint); cap the read
        // so a malformed file cannot make us allocate an arbitrary array.
        count = Math.Min(count, 8);

        var contexts = stackalloc IWICColorContext*[(int)count];
        for (var i = 0u; i < count; i++) contexts[i] = null;

        try
        {
            // GetColorContexts fills objects the caller supplies rather than creating them.
            for (var i = 0u; i < count; i++)
            {
                if (factory->CreateColorContext(&contexts[i]).Failure) return;
            }

            uint actual = 0;
            if (frame->GetColorContexts(count, contexts, &actual).Failure) return;

            for (var i = 0u; i < actual; i++)
            {
                var context = contexts[i];
                if (context == null) continue;

                WICColorContextType type;
                if (context->GetType(&type).Failure) continue;

                if (type == WIC.WICColorContextProfile && TryPublishProfile(context, outInfo)) return;

                if (type == WIC.WICColorContextExifColorSpace)
                {
                    uint exifSpace = 0;
                    if (context->GetExifColorSpace(&exifSpace).Failure) continue;

                    // 1 = sRGB, 2 = Adobe RGB; anything else is "uncalibrated".
                    outInfo->ColorSpace = (int)(exifSpace switch
                    {
                        1 => IGColorSpace.Srgb,
                        2 => IGColorSpace.AdobeRgb,
                        _ => IGColorSpace.Unknown,
                    });
                }
            }
        }
        finally
        {
            for (var i = 0u; i < count; i++)
            {
                ComInterop.Release(ref contexts[i]);
            }
        }
    }


    private static bool TryPublishProfile(IWICColorContext* context, IGImageInfo* outInfo)
    {
        uint size = 0;
        if (context->GetProfileBytes(0, null, &size).Failure || size == 0) return false;

        var bytes = new byte[size];
        fixed (byte* pBytes = bytes)
        {
            if (context->GetProfileBytes(size, pBytes, &size).Failure) return false;
        }

        var block = NativeBuffers.PublishIccProfile(bytes.AsSpan(0, (int)Math.Min(size, bytes.Length)));
        if (block == null) return false;

        outInfo->IccProfileData = block;
        outInfo->IccProfileSize = (int)Math.Min(size, bytes.Length);
        return true;
    }


    private static long FileSizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }
}
