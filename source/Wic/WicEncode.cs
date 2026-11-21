/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using ImageGlass.SDK.Plugins;
using System.Runtime.InteropServices;
using Vortice.Win32;
using Vortice.Win32.Com;
using Vortice.Win32.Graphics;

namespace WicCodec.Wic;

/// <summary>
/// Writes images through WIC.
/// <para>
/// The host always hands over a temp path it owns and moves the result into place itself, so
/// the one hard requirement here is that every file handle is closed before returning — on the
/// failure paths too, or the move fails with a sharing violation.
/// </para>
/// </summary>
internal static unsafe class WicEncode
{
    /// <summary>
    /// Writes a single image. Returns <see cref="IGStatus.Unsupported"/> when no installed
    /// encoder claims the extension, which makes the host fall back to its own encoder.
    /// </summary>
    public static IGStatus EncodeStatic(string destPath, IGPixelBuffer* pixels,
        IGEncodeOptions* options, void* cancellation)
    {
        if (pixels == null || pixels->Data == null) return IGStatus.InvalidArg;
        if (!WicFormats.TryGetEncoder(Path.GetExtension(destPath), out var target))
        {
            return IGStatus.Unsupported;
        }

        IWICImagingFactory* factory = null;
        IWICStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* bag = null;

        try
        {
            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            factory = WicFactory.Create();
            if (factory == null) return IGStatus.Internal;

            var status = OpenEncoder(factory, destPath, target, &stream, &encoder);
            if (status != IGStatus.OK) return status;

            if (encoder->CreateNewFrame(&frame, &bag).Failure) return IGStatus.EncodeFailed;

            ApplyEncoderOptions(bag, options, pixels);
            if (frame->Initialize(bag).Failure) return IGStatus.EncodeFailed;

            status = WriteFrame(factory, frame, pixels, options, target, 0, 0, cancellation);
            if (status != IGStatus.OK) return status;

            return encoder->Commit().Failure ? IGStatus.IoError : IGStatus.OK;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: EncodeStaticRaster failed. {ex}");
            return IGStatus.Internal;
        }
        finally
        {
            // Release order matters: the stream is what holds the file handle, and it must go
            // last so the encoder cannot touch it after it is gone.
            ComInterop.Release(ref bag);
            ComInterop.Release(ref frame);
            ComInterop.Release(ref encoder);
            ComInterop.Release(ref stream);
            ComInterop.Release(ref factory);
        }
    }


    /// <summary>
    /// One open multi-frame encode. Every call for a session arrives on the thread that opened
    /// it, so nothing here is synchronized.
    /// </summary>
    private sealed class Session
    {
        public IWICImagingFactory* Factory;
        public IWICStream* Stream;
        public IWICBitmapEncoder* Encoder;
        public EncoderTarget Target;
        public bool IsAnimated;
        public int LoopCount;

        // Scalars only. The IGEncodeOptions the host passes to Begin points at host memory that
        // is valid for that ONE call -- IccProfileData is a pinned managed array whose pin is
        // released the moment Begin returns -- so keeping the struct and reusing it on later
        // frames would hand a moved-or-freed pointer to WIC. Copy what we need instead.
        public int Quality;
        public bool Lossless;
        public bool PreserveAlpha;
        public byte* Icc;
        public int IccSize;

        /// <summary>Takes a plugin-owned copy of everything the session needs past Begin.</summary>
        public void CaptureOptions(IGEncodeOptions* options)
        {
            Quality = options != null ? options->Quality : 100;
            Lossless = options != null && options->Lossless != 0;
            PreserveAlpha = options == null || options->PreserveAlpha != 0;

            if (options == null || options->IccProfileData == null || options->IccProfileSize <= 0) return;

            Icc = (byte*)NativeMemory.Alloc((nuint)options->IccProfileSize);
            new ReadOnlySpan<byte>(options->IccProfileData, options->IccProfileSize)
                .CopyTo(new Span<byte>(Icc, options->IccProfileSize));
            IccSize = options->IccProfileSize;
        }

        /// <summary>Rebuilds per-frame options over the plugin-owned ICC copy.</summary>
        public IGEncodeOptions FrameOptions() => new()
        {
            StructSize = sizeof(IGEncodeOptions),
            Quality = Quality,
            Lossless = Lossless ? 1 : 0,
            PreserveAlpha = PreserveAlpha ? 1 : 0,
            SourceFilePath = default,
            IccProfileData = Icc,
            IccProfileSize = IccSize,
        };

        public void ReleaseAll()
        {
            ComInterop.Release(ref Encoder);
            ComInterop.Release(ref Stream);
            ComInterop.Release(ref Factory);

            if (Icc != null) { NativeMemory.Free(Icc); Icc = null; IccSize = 0; }
        }
    }


    /// <summary>
    /// Opens a multi-frame session (multi-page TIFF, multi-size ICO, animated GIF, HEIF
    /// sequence). Declines when the chosen container is single-frame only, so the host can
    /// fall back rather than write a file with the frames silently dropped.
    /// </summary>
    public static IGStatus BeginMultiFrame(string destPath, IGMultiFrameEncodeInfo* info,
        IGEncodeOptions* options, void** outSession, void* cancellation)
    {
        if (outSession == null) return IGStatus.InvalidArg;
        *outSession = null;

        if (!WicFormats.TryGetEncoder(Path.GetExtension(destPath), out var target)
            || !target.SupportsMultiFrame)
        {
            return IGStatus.Unsupported;
        }

        var session = new Session
        {
            Target = target,
            IsAnimated = info != null && info->IsAnimated != 0 && target.SupportsAnimation,
            LoopCount = info != null ? info->LoopCount : 0,
        };
        session.CaptureOptions(options);

        try
        {
            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            session.Factory = WicFactory.Create();
            if (session.Factory == null) return IGStatus.Internal;

            IWICStream* stream = null;
            IWICBitmapEncoder* encoder = null;
            var status = OpenEncoder(session.Factory, destPath, target, &stream, &encoder);
            session.Stream = stream;
            session.Encoder = encoder;
            if (status != IGStatus.OK) return status;

            if (session.IsAnimated) WriteLoopExtension(session);

            // The session pointer is a GCHandle: the host only ever passes it back to us.
            *outSession = (void*)GCHandle.ToIntPtr(GCHandle.Alloc(session));
            return IGStatus.OK;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: BeginEncodeMultiFrame failed. {ex}");
            return IGStatus.Internal;
        }
        finally
        {
            // A non-OK Begin means the host will not call End, so nothing may stay open.
            if (*outSession == null) session.ReleaseAll();
        }
    }


    /// <summary>Appends one frame to an open session.</summary>
    public static IGStatus EncodeFrame(void* sessionPtr, IGPixelBuffer* pixels,
        IGEncodeFrameInfo* frameInfo, void* cancellation)
    {
        if (!TryResolve(sessionPtr, out var session)) return IGStatus.InvalidArg;
        if (pixels == null || pixels->Data == null) return IGStatus.InvalidArg;

        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* bag = null;

        try
        {
            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;
            if (session.Encoder->CreateNewFrame(&frame, &bag).Failure) return IGStatus.EncodeFailed;

            var options = session.FrameOptions();
            ApplyEncoderOptions(bag, &options, pixels);
            if (frame->Initialize(bag).Failure) return IGStatus.EncodeFailed;

            var duration = frameInfo != null && session.IsAnimated ? frameInfo->DurationMs : 0;
            var index = frameInfo != null ? frameInfo->FrameIndex : 0;

            return WriteFrame(session.Factory, frame, pixels, &options, session.Target,
                duration, index, cancellation);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: EncodeFrame failed. {ex}");
            return IGStatus.Internal;
        }
        finally
        {
            ComInterop.Release(ref bag);
            ComInterop.Release(ref frame);
        }
    }


    /// <summary>
    /// Closes a session. Frees the handle and every COM object on both the commit and the
    /// abort path; the host discards the temp file when <paramref name="commit"/> is 0.
    /// </summary>
    public static IGStatus EndMultiFrame(void* sessionPtr, int commit, void* cancellation)
    {
        if (!TryResolve(sessionPtr, out var session)) return IGStatus.InvalidArg;

        try
        {
            if (commit == 0) return IGStatus.OK;
            return session.Encoder->Commit().Failure ? IGStatus.IoError : IGStatus.OK;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: EndEncodeMultiFrame failed. {ex}");
            return IGStatus.Internal;
        }
        finally
        {
            session.ReleaseAll();
            GCHandle.FromIntPtr((nint)sessionPtr).Free();
        }
    }


    private static bool TryResolve(void* sessionPtr, out Session session)
    {
        session = null!;
        if (sessionPtr == null) return false;

        var handle = GCHandle.FromIntPtr((nint)sessionPtr);
        if (handle.Target is not Session resolved) return false;

        session = resolved;
        return session.Encoder != null;
    }


    /// <summary>
    /// Creates the file stream and the container encoder bound to it.
    /// </summary>
    private static IGStatus OpenEncoder(IWICImagingFactory* factory, string destPath,
        in EncoderTarget target, IWICStream** outStream, IWICBitmapEncoder** outEncoder)
    {
        if (factory->CreateStream(outStream).Failure) return IGStatus.Internal;

        fixed (char* pPath = destPath)
        {
            if ((*outStream)->InitializeFromFilename(pPath, ComInterop.GENERIC_WRITE).Failure)
            {
                return IGStatus.IoError;
            }
        }

        var container = target.ContainerFormat;
        if (factory->CreateEncoder(&container, null, outEncoder).Failure) return IGStatus.Unsupported;

        return (*outEncoder)->Initialize((IStream*)*outStream, WIC.WICBitmapEncoderNoCache).Failure
            ? IGStatus.EncodeFailed
            : IGStatus.OK;
    }


    /// <summary>
    /// Wraps the host's pixels as a WIC bitmap and writes them through
    /// <c>WriteSource</c>, which negotiates whatever pixel format the container needs
    /// (palettized GIF, 24bpp BMP, 8bpp gray, ...) instead of us hardcoding a table.
    /// </summary>
    private static IGStatus WriteFrame(IWICImagingFactory* factory, IWICBitmapFrameEncode* frame,
        IGPixelBuffer* pixels, IGEncodeOptions* options, in EncoderTarget target,
        int durationMs, int frameIndex, void* cancellation)
    {
        var format = MapSourceFormat((IGPixelFormat)pixels->PixelFormat);
        if (format == Guid.Empty) return IGStatus.Unsupported;

        var width = (uint)pixels->Width;
        var height = (uint)pixels->Height;
        var byteCount = (long)pixels->Stride * height;
        if (width == 0 || height == 0 || byteCount <= 0 || byteCount > int.MaxValue)
        {
            return IGStatus.InvalidArg;
        }

        IWICBitmap* bitmap = null;
        try
        {
            if (factory->CreateBitmapFromMemory(width, height, &format, (uint)pixels->Stride,
                (uint)byteCount, pixels->Data, &bitmap).Failure)
            {
                return IGStatus.Unsupported;
            }

            if (frame->SetSize(width, height).Failure) return IGStatus.EncodeFailed;

            // SetPixelFormat is in/out: the encoder rewrites it to the closest format it
            // supports, and WriteSource then converts into that.
            var requested = options != null && options->PreserveAlpha == 0
                ? WIC.GUID_WICPixelFormat24bppBGR
                : format;
            frame->SetPixelFormat(&requested);
            frame->SetResolution(96.0, 96.0);

            ApplySourceColorContext(factory, frame, options);
            if (durationMs > 0) ApplyFrameDelay(frame, target, durationMs);

            if (HostChannel.IsCanceled(cancellation)) return IGStatus.Canceled;

            if (frame->WriteSource((IWICBitmapSource*)bitmap, null).Failure)
            {
                HostChannel.Log(4, $"WicCodec: encoder refused frame {frameIndex}.");
                return IGStatus.EncodeFailed;
            }

            return frame->Commit().Failure ? IGStatus.EncodeFailed : IGStatus.OK;
        }
        finally
        {
            ComInterop.Release(ref bitmap);
        }
    }


    /// <summary>
    /// Tags the output with the profile the host says the pixels are in. Encoders that cannot
    /// carry a profile fail this call, which is not an error.
    /// </summary>
    private static void ApplySourceColorContext(IWICImagingFactory* factory,
        IWICBitmapFrameEncode* frame, IGEncodeOptions* options)
    {
        if (options == null || options->IccProfileData == null || options->IccProfileSize <= 0) return;

        IWICColorContext* context = null;
        try
        {
            if (factory->CreateColorContext(&context).Failure) return;
            if (context->InitializeFromMemory(options->IccProfileData, (uint)options->IccProfileSize).Failure)
            {
                return;
            }

            frame->SetColorContexts(1, &context);
        }
        finally
        {
            ComInterop.Release(ref context);
        }
    }


    /// <summary>
    /// Writes the per-frame delay for animated containers. GIF stores it in hundredths of a
    /// second in the graphic control extension.
    /// </summary>
    private static void ApplyFrameDelay(IWICBitmapFrameEncode* frame, in EncoderTarget target, int durationMs)
    {
        if (target.ContainerFormat != WIC.GUID_ContainerFormatGif) return;

        IWICMetadataQueryWriter* writer = null;
        try
        {
            if (frame->GetMetadataQueryWriter(&writer).Failure) return;

            var hundredths = (ushort)Math.Clamp((durationMs + 5) / 10, 1, ushort.MaxValue);
            var value = ComInterop.PropVariant.FromUInt16(hundredths);
            fixed (char* pQuery = "/grctlext/Delay")
            {
                writer->SetMetadataByName(pQuery, (Variant*)&value);
            }
        }
        finally
        {
            ComInterop.Release(ref writer);
        }
    }


    /// <summary>
    /// Writes the NETSCAPE2.0 application extension that carries a GIF's loop count.
    /// </summary>
    private static void WriteLoopExtension(Session session)
    {
        if (session.Target.ContainerFormat != WIC.GUID_ContainerFormatGif) return;

        IWICMetadataQueryWriter* writer = null;
        try
        {
            if (session.Encoder->GetMetadataQueryWriter(&writer).Failure) return;

            var application = "NETSCAPE2.0"u8;
            var loops = (ushort)Math.Clamp(session.LoopCount, 0, ushort.MaxValue);
            Span<byte> data = [3, 1, (byte)(loops & 0xFF), (byte)(loops >> 8)];

            fixed (byte* pApplication = application)
            fixed (byte* pData = data)
            {
                var appValue = ComInterop.PropVariant.FromByteVector(pApplication, application.Length);
                fixed (char* pAppQuery = "/appext/Application")
                {
                    writer->SetMetadataByName(pAppQuery, (Variant*)&appValue);
                }

                var dataValue = ComInterop.PropVariant.FromByteVector(pData, data.Length);
                fixed (char* pDataQuery = "/appext/Data")
                {
                    writer->SetMetadataByName(pDataQuery, (Variant*)&dataValue);
                }
            }
        }
        finally
        {
            ComInterop.Release(ref writer);
        }
    }


    /// <summary>
    /// Writes the quality knobs the chosen encoder actually declares. The property bag is
    /// enumerated first because writing an unknown property fails the whole call, and every
    /// container names its options differently.
    /// </summary>
    private static void ApplyEncoderOptions(IPropertyBag2* bag, IGEncodeOptions* options,
        IGPixelBuffer* pixels)
    {
        if (bag == null) return;

        var quality = options != null ? Math.Clamp(options->Quality, 1, 100) : 100;
        var lossless = options != null && options->Lossless != 0;
        var hasAlpha = options == null || options->PreserveAlpha != 0;

        uint count = 0;
        if (bag->CountProperties(&count).Failure || count == 0) return;
        count = Math.Min(count, 64);

        for (var i = 0u; i < count; i++)
        {
            ComInterop.PropBag2 meta = default;
            uint read = 0;
            if (bag->GetPropertyInfo(i, 1, (PropertyBagMetadata*)&meta, &read).Failure || read != 1) continue;

            try
            {
                if (meta.Name == null) continue;
                var name = new string(meta.Name);

                ComInterop.PropVariant value;
                switch (name)
                {
                    // JPEG, JPEG XR, HEIF, JPEG XL: 0..1.
                    case "ImageQuality":
                        value = ComInterop.PropVariant.FromFloat(quality / 100f);
                        break;

                    // TIFF names the same knob differently.
                    case "CompressionQuality":
                        value = ComInterop.PropVariant.FromFloat(quality / 100f);
                        break;

                    case "Lossless":
                        value = ComInterop.PropVariant.FromBool(lossless);
                        break;

                    // Deliberately forced off, never on. Turning it on makes the JPEG XR encoder
                    // ignore ImageQuality and Lossless in favour of its own codec-level knobs
                    // (Quality, Overlap, Subsampling), which we do not populate -- so a "lossless"
                    // save would silently come back quantized at the codec's default quality.
                    case "UseCodecOptions":
                        value = ComInterop.PropVariant.FromBool(false);
                        break;

                    // The BMP encoder drops the alpha channel unless it is allowed to emit a
                    // V5 header, which is a silent data loss on any transparent image.
                    case "EnableV5Header32bppBGRA":
                        value = ComInterop.PropVariant.FromBool(hasAlpha && HasAlphaFormat(pixels));
                        break;

                    default:
                        continue;
                }

                bag->Write(1, (PropertyBagMetadata*)&meta, (Variant*)&value);
            }
            finally
            {
                if (meta.Name != null) Marshal.FreeCoTaskMem((nint)meta.Name);
            }
        }
    }


    private static bool HasAlphaFormat(IGPixelBuffer* pixels)
        => pixels != null && (IGPixelFormat)pixels->PixelFormat is
            IGPixelFormat.Bgra8Unorm or IGPixelFormat.Rgba8Unorm
            or IGPixelFormat.Rgba16Unorm or IGPixelFormat.RgbaFloat16;


    /// <summary>
    /// The WIC pixel format matching what the host hands over. The host normalizes to
    /// unpremultiplied BGRA8 today; the others are here so a future widening is not a silent
    /// mis-decode.
    /// </summary>
    private static Guid MapSourceFormat(IGPixelFormat format) => format switch
    {
        IGPixelFormat.Bgra8Unorm => WIC.GUID_WICPixelFormat32bppBGRA,
        IGPixelFormat.Rgba8Unorm => WIC.GUID_WICPixelFormat32bppRGBA,
        IGPixelFormat.Rgba16Unorm => WIC.GUID_WICPixelFormat64bppRGBA,
        IGPixelFormat.RgbaFloat16 => WIC.GUID_WICPixelFormat64bppRGBAHalf,
        _ => Guid.Empty,
    };
}
