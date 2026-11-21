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
/// The animation codec: GIF as a timeline, with every frame fully composed on the canvas.
/// </summary>
internal static unsafe class WicAnimation
{
    public static IGStatus LoadMetadata(string path, IGImageInfo* outInfo, void* cancellation)
    {
        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;

        try
        {
            var status = WicDecode.Open(path, cancellation, ref factory, ref decoder);
            if (status != IGStatus.OK) return status;

            Guid container;
            if (decoder->GetContainerFormat(&container).Failure) container = default;

            if (container == WIC.GUID_ContainerFormatGif)
            {
                var timeline = GetTimeline(path, decoder, container);
                if (timeline is null) return IGStatus.DecodeFailed;

                // The canvas is the logical screen, not frame 0, which may be a smaller patch of it.
                outInfo->Width = timeline.Width;
                outInfo->Height = timeline.Height;
                outInfo->PixelFormat = (int)IGPixelFormat.Bgra8Unorm;
                outInfo->HasAlpha = timeline.HasAlpha ? 1 : 0;
                outInfo->HdrTransferFn = (int)IGHdrTransferFn.None;
                outInfo->Orientation = 1;
                outInfo->FrameCount = timeline.FrameCount;
                outInfo->FileSizeBytes = WicDecode.FileSizeOf(path);
                return IGStatus.OK;
            }

            status = WicDecode.FillMetadata(factory, decoder, path, outInfo);
            if (status != IGStatus.OK) return status;

            // Mislabeled content (a TIFF named .gif): FrameCount > 1 would start the animator on a page stack.
            outInfo->FrameCount = Math.Min(outInfo->FrameCount, 1);
            return IGStatus.OK;
        }
        finally
        {
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref factory);
        }
    }


    public static IGStatus GetAnimationInfo(string path, IGAnimationInfo* outInfo, void* cancellation)
    {
        var timeline = GetTimeline(path, cancellation, out var status);
        if (timeline is null) return status;

        var count = timeline.FrameCount;
        var frames = NativeBuffers.AllocFrameTable(count);
        for (var i = 0; i < count; i++)
        {
            frames[i] = new IGAnimationFrameInfo
            {
                DurationMs = timeline.DurationsMs[i],
                HasAlpha = timeline.FrameHasAlpha[i] ? 1 : 0,
            };
        }

        outInfo->FrameCount = count;
        outInfo->LoopCount = timeline.LoopCount;
        outInfo->Frames = frames;
        return IGStatus.OK;
    }


    public static void FreeAnimationInfo(IGAnimationInfo* info)
    {
        if (!NativeBuffers.FreeFrameTable(info->Frames)) return;

        info->Frames = null;
        info->FrameCount = 0;
    }


    public static IGStatus DecodeAnimationFrame(string path, int frameIndex, IGPixelBuffer* outBuf, void* cancellation)
    {
        var timeline = GetTimeline(path, cancellation, out var status);
        if (timeline is null) return status;
        if (frameIndex < 0 || frameIndex >= timeline.FrameCount) return IGStatus.InvalidArg;

        return AnimWorker.Render(path, timeline, frameIndex, outBuf, cancellation);
    }


    /// <summary>
    /// One still frame for the static path (thumbnails, previews, single-frame files); 0 for the box means full size.
    /// </summary>
    public static IGStatus DecodeStill(string path, int frameIndex, int maxWidth, int maxHeight,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;
        AnimTimeline? timeline = null;

        try
        {
            var status = WicDecode.Open(path, cancellation, ref factory, ref decoder);
            if (status != IGStatus.OK) return status;

            Guid container;
            if (decoder->GetContainerFormat(&container).Failure) container = default;

            // Mislabeled content is a plain WIC still.
            if (container != WIC.GUID_ContainerFormatGif)
            {
                ComInterop.Release(ref decoder);
                return maxWidth > 0 && maxHeight > 0
                    ? WicDecode.DecodeScaled(path, frameIndex, maxWidth, maxHeight, outBuf, cancellation)
                    : WicDecode.Decode(path, frameIndex, outBuf, cancellation);
            }

            timeline = GetTimeline(path, decoder, container);
            if (timeline is null) return IGStatus.DecodeFailed;
            if (frameIndex < 0 || frameIndex >= timeline.FrameCount) return IGStatus.InvalidArg;

            if (frameIndex == 0)
            {
                // Frame 0 needs no history, so the thumbnail path composes it here and stays parallel.
                var canvas = new byte[timeline.Width * timeline.Height * 4];
                status = AnimPlayer.DrawGifFrame(factory, decoder, timeline, 0, canvas, new GifScratch());
                if (status != IGStatus.OK) return status;

                status = AnimPlayer.Publish(canvas, timeline.Width, timeline.Height, outBuf);
            }
            else
            {
                // Later frames depend on the ones before, which the worker's player already tracks.
                ComInterop.Release(ref decoder);
                status = AnimWorker.Render(path, timeline, frameIndex, outBuf, cancellation);
            }

            if (status != IGStatus.OK || maxWidth <= 0 || maxHeight <= 0) return status;
            return FitInto(factory, outBuf, maxWidth, maxHeight);
        }
        finally
        {
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref factory);
        }
    }


    /// <summary>
    /// Shrinks a BGRA buffer this plugin handed out so it fits the box, replacing it in place.
    /// </summary>
    private static IGStatus FitInto(IWICImagingFactory* factory, IGPixelBuffer* buf, int maxWidth, int maxHeight)
    {
        IWICBitmap* bitmap = null;
        IWICBitmapScaler* scaler = null;
        byte* pixels = null;

        try
        {
            var format = WIC.GUID_WICPixelFormat32bppBGRA;
            var byteCount = (uint)(buf->Stride * buf->Height);
            if (factory->CreateBitmapFromMemory((uint)buf->Width, (uint)buf->Height, &format,
                (uint)buf->Stride, byteCount, (byte*)buf->Data, &bitmap).Failure)
            {
                return IGStatus.Internal;
            }

            var scaled = WicDecode.TryScale(factory, (IWICBitmapSource*)bitmap, 1,
                (uint)maxWidth, (uint)maxHeight, &scaler);
            if (scaled == null) return IGStatus.OK;

            uint width = 0, height = 0;
            if (scaled->GetSize(&width, &height).Failure) return IGStatus.OK;

            var stride = width * 4;
            pixels = NativeBuffers.AllocPixels((nuint)(stride * height));
            if (pixels == null) return IGStatus.OutOfMemory;
            if (scaled->CopyPixels(null, stride, stride * height, pixels).Failure) return IGStatus.OK;

            NativeBuffers.FreePixels(buf->Data);
            buf->Data = pixels;
            buf->ReleaseContext = pixels;
            buf->Width = (int)width;
            buf->Height = (int)height;
            buf->Stride = (int)stride;
            pixels = null;
            return IGStatus.OK;
        }
        finally
        {
            // Only non-null when the full-size buffer is kept instead.
            NativeBuffers.Discard(pixels);
            ComInterop.Release(ref scaler);
            ComInterop.Release(ref bitmap);
        }
    }


    /// <summary>The cached timeline for the file's current version, read through a fresh decoder on a miss.</summary>
    private static AnimTimeline? GetTimeline(string path, void* cancellation, out IGStatus status)
    {
        if (!AnimTimelineCache.TryStamp(path, out var stamp, out var size))
        {
            status = IGStatus.IoError;
            return null;
        }

        var cached = AnimTimelineCache.Get(path, stamp, size);
        if (cached is not null)
        {
            status = IGStatus.OK;
            return cached;
        }

        IWICImagingFactory* factory = null;
        IWICBitmapDecoder* decoder = null;
        try
        {
            status = WicDecode.Open(path, cancellation, ref factory, ref decoder);
            if (status != IGStatus.OK) return null;

            Guid container;
            if (decoder->GetContainerFormat(&container).Failure) container = default;

            var timeline = GetTimeline(path, decoder, container);
            status = timeline is null ? IGStatus.Unsupported : IGStatus.OK;
            return timeline;
        }
        finally
        {
            ComInterop.Release(ref decoder);
            ComInterop.Release(ref factory);
        }
    }


    private static AnimTimeline? GetTimeline(string path, IWICBitmapDecoder* decoder, Guid container)
    {
        if (!AnimTimelineCache.TryStamp(path, out var stamp, out var size)) return null;

        var timeline = AnimTimelineCache.Get(path, stamp, size);
        if (timeline is not null) return timeline;

        timeline = AnimTimeline.Read(decoder, container);
        if (timeline is not null) AnimTimelineCache.Put(path, stamp, size, timeline);
        return timeline;
    }
}
