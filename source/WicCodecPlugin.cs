/*
ImageGlass WIC Codec Plugin
Copyright (C) 2026 DUONG DIEU PHAP
MIT License
https://github.com/d2phap/wic-imageglass-plugin
*/
using ImageGlass.SDK.Plugins;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WicCodec.Wic;

namespace WicCodec;

internal static unsafe class WicCodecPlugin
{
    // ------------------------------ Static buffers ------------------------------
    // Everything the host receives must outlive the call. The API tables, id strings and the
    // extension tables are process-lifetime native blocks that are intentionally never freed.

    private const string PluginIdString = "Plugin_WicCodec";
    private const string PluginNameString = "WIC Codec";
    private const string VersionString = "1.2.0";
    private const string CodecIdString = "plugin.wic.codec";
    private const string CodecNameString = "WIC Codec";
    private const string AnimCodecIdString = "plugin.wic.animation";
    private const string AnimCodecNameString = "WIC Animation";

    // Above SkiaSharp (100) and Magick.NET (10/100) for every extension WIC claims. Enabling a
    // plugin is an explicit act of trust, so the host honors this verbatim; the per-extension
    // tick boxes in Settings > Plugins are how a user hands an individual format back.
    private const int Priority = 300;

    // IGHdrTransferFn.ScRgb arrived in ABI minor 1; an older host maps the unknown value to None
    // and skips tone mapping entirely, which blows out every extended-range image.
    private const int ScRgbHostAbiVersion = 1_001_000;

    private static IGPluginApi* _pluginApi;
    private static IGCodecApi* _codecApi;
    private static IGCodecCapability* _capability;

    // A second codec because animation is a per-codec flag and the host animates every FrameCount > 1 file of such a codec, page stacks included.
    private static IGCodecApi* _animCodecApi;
    private static IGCodecCapability* _animCapability;


    // ------------------------------ Entry point ------------------------------
    //
    // Every [UnmanagedCallersOnly] entry point below is a hard ABI boundary: an escaping managed
    // exception cannot be caught by the host and kills the app, so each one ends in a catch.

    [UnmanagedCallersOnly(EntryPoint = IGNativeAbi.ENTRY_POINT_NAME, CallConvs = [typeof(CallConvCdecl)])]
    public static IGPluginApi* GetApi(int hostAbiVersion, IGHostApi* hostApi)
    {
        // Major-version mismatch: refuse to load.
        if (hostAbiVersion / 1_000_000 != IGNativeAbi.IG_PLUGIN_ABI_MAJOR) return null;
        if (hostApi == null) return null;

        WicPixels.HostSupportsScRgb = hostAbiVersion >= ScRgbHostAbiVersion;

        if (_pluginApi != null) return _pluginApi;
        HostChannel.Attach(hostApi);

        try
        {
            WicFormats.Discover();
            InitCapability();
            InitCodecApi();
            InitAnimationCodec();
            InitPluginApi();
        }
        catch (Exception ex)
        {
            // null is the ABI's "refused"; the host logs it and skips the plugin
            HostChannel.Log(4, $"WicCodec: initialization failed. {ex}");
            return null;
        }

        return _pluginApi;
    }


    // ------------------------------ Plugin API callbacks ------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus OnInitialize() => IGStatus.OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShutdown()
    {
        // The worker thread holds decoders and file copies; nothing may run once the host unloads us.
        try { AnimWorker.Stop(); } catch { }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus OnGetCodec(int index, IGCodecApi** outCodecApi)
    {
        if (outCodecApi == null) return IGStatus.InvalidArg;

        *outCodecApi = index switch
        {
            0 => _codecApi,
            1 => _animCodecApi,
            _ => null,
        };
        return *outCodecApi != null ? IGStatus.OK : IGStatus.InvalidArg;
    }


    // ------------------------------ Codec API callbacks ------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecGetCapability(IGCodecCapability** outCap)
    {
        if (outCap == null) return IGStatus.InvalidArg;

        *outCap = _capability;
        return IGStatus.OK;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CodecCanHandleExtension(IGStringRef ext)
    {
        if (ext.Data == null || ext.Length <= 0) return 0;

        var value = new ReadOnlySpan<char>(ext.Data, ext.Length);
        foreach (var supported in WicFormats.DecodeExtensions)
        {
            if (value.Equals(supported, StringComparison.OrdinalIgnoreCase)) return 1;
        }
        return 0;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecLoadMetadata(IGStringRef filePath, IGImageInfo* outInfo, void* cancellation)
    {
        if (outInfo == null) return IGStatus.InvalidArg;
        *outInfo = default;

        try
        {
            if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
            return WicDecode.LoadMetadata(path, outInfo, cancellation);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: LoadMetadata failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecDecodeStaticRaster(IGStringRef filePath, int frameIndex,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        if (outBuf == null) return IGStatus.InvalidArg;
        *outBuf = default;

        try
        {
            if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
            return WicDecode.Decode(path, frameIndex, outBuf, cancellation);
        }
        catch (OutOfMemoryException)
        {
            return IGStatus.OutOfMemory;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: DecodeStaticRaster failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecDecodeStaticRasterScaled(IGStringRef filePath, int frameIndex,
        int maxWidth, int maxHeight, IGPixelBuffer* outBuf, void* cancellation)
    {
        if (outBuf == null) return IGStatus.InvalidArg;
        *outBuf = default;

        try
        {
            if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
            return WicDecode.DecodeScaled(path, frameIndex, maxWidth, maxHeight, outBuf, cancellation);
        }
        catch (OutOfMemoryException)
        {
            return IGStatus.OutOfMemory;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: DecodeStaticRasterScaled failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CodecFreePixelBuffer(IGPixelBuffer* buf)
    {
        if (buf == null || buf->Data == null) return;

        // Callable from any thread, including the GC finalizer thread. Buffers the plugin never
        // allocated are refused, which covers the host-owned pixels passed IN to encoding.
        try
        {
            if (!NativeBuffers.FreePixels(buf->Data)) return;

            buf->Data = null;
            buf->ReleaseContext = null;
        }
        catch { }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecEncodeStaticRaster(IGStringRef destFilePath, IGPixelBuffer* pixels,
        IGEncodeOptions* options, void* cancellation)
    {
        try
        {
            if (!TryGetPath(destFilePath, out var path)) return IGStatus.InvalidArg;
            return WicEncode.EncodeStatic(path, pixels, options, cancellation);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: EncodeStaticRaster failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecBeginEncodeMultiFrame(IGStringRef destFilePath,
        IGMultiFrameEncodeInfo* info, IGEncodeOptions* options, void** outSession, void* cancellation)
    {
        try
        {
            if (!TryGetPath(destFilePath, out var path)) return IGStatus.InvalidArg;
            return WicEncode.BeginMultiFrame(path, info, options, outSession, cancellation);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: BeginEncodeMultiFrame failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecEncodeFrame(void* session, IGPixelBuffer* frame,
        IGEncodeFrameInfo* frameInfo, void* cancellation)
    {
        try
        {
            return WicEncode.EncodeFrame(session, frame, frameInfo, cancellation);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: EncodeFrame failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus CodecEndEncodeMultiFrame(void* session, int commit, void* cancellation)
    {
        try
        {
            return WicEncode.EndMultiFrame(session, commit, cancellation);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: EndEncodeMultiFrame failed. {ex}");
            return IGStatus.Internal;
        }
    }


    // ------------------------------ Animation codec callbacks ------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus AnimGetCapability(IGCodecCapability** outCap)
    {
        if (outCap == null) return IGStatus.InvalidArg;

        *outCap = _animCapability;
        return IGStatus.OK;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AnimCanHandleExtension(IGStringRef ext)
    {
        if (ext.Data == null || ext.Length <= 0) return 0;

        var value = new ReadOnlySpan<char>(ext.Data, ext.Length);
        foreach (var supported in WicFormats.AnimationExtensions)
        {
            if (value.Equals(supported, StringComparison.OrdinalIgnoreCase)) return 1;
        }
        return 0;
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus AnimLoadMetadata(IGStringRef filePath, IGImageInfo* outInfo, void* cancellation)
    {
        if (outInfo == null) return IGStatus.InvalidArg;
        *outInfo = default;

        try
        {
            if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
            return WicAnimation.LoadMetadata(path, outInfo, cancellation);
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: animation LoadMetadata failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus AnimDecodeStaticRaster(IGStringRef filePath, int frameIndex,
        IGPixelBuffer* outBuf, void* cancellation)
        => AnimDecodeStill(filePath, frameIndex, 0, 0, outBuf, cancellation);


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus AnimDecodeStaticRasterScaled(IGStringRef filePath, int frameIndex,
        int maxWidth, int maxHeight, IGPixelBuffer* outBuf, void* cancellation)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return IGStatus.InvalidArg;
        return AnimDecodeStill(filePath, frameIndex, maxWidth, maxHeight, outBuf, cancellation);
    }


    private static IGStatus AnimDecodeStill(IGStringRef filePath, int frameIndex, int maxWidth, int maxHeight,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        if (outBuf == null) return IGStatus.InvalidArg;
        *outBuf = default;

        try
        {
            if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
            return WicAnimation.DecodeStill(path, frameIndex, maxWidth, maxHeight, outBuf, cancellation);
        }
        catch (OutOfMemoryException)
        {
            return IGStatus.OutOfMemory;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: animation still decode failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus AnimGetAnimationInfo(IGStringRef filePath, IGAnimationInfo* outInfo, void* cancellation)
    {
        if (outInfo == null) return IGStatus.InvalidArg;
        *outInfo = default;

        try
        {
            if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
            return WicAnimation.GetAnimationInfo(path, outInfo, cancellation);
        }
        catch (OutOfMemoryException)
        {
            return IGStatus.OutOfMemory;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: GetAnimationInfo failed. {ex}");
            return IGStatus.Internal;
        }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AnimFreeAnimationInfo(IGAnimationInfo* info)
    {
        if (info == null || info->Frames == null) return;

        try
        {
            WicAnimation.FreeAnimationInfo(info);
        }
        catch { }
    }


    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IGStatus AnimDecodeAnimationFrame(IGStringRef filePath, int frameIndex,
        IGPixelBuffer* outBuf, void* cancellation)
    {
        if (outBuf == null) return IGStatus.InvalidArg;
        *outBuf = default;

        try
        {
            if (!TryGetPath(filePath, out var path)) return IGStatus.InvalidArg;
            return WicAnimation.DecodeAnimationFrame(path, frameIndex, outBuf, cancellation);
        }
        catch (OutOfMemoryException)
        {
            return IGStatus.OutOfMemory;
        }
        catch (Exception ex)
        {
            HostChannel.Log(4, $"WicCodec: DecodeAnimationFrame failed. {ex}");
            return IGStatus.Internal;
        }
    }


    // ------------------------------ Table construction ------------------------------

    private static void InitCapability()
    {
        var decodeExtensions = WicFormats.DecodeExtensions;
        var encodeExtensions = WicFormats.EncodeExtensions;

        _capability = (IGCodecCapability*)NativeMemory.AllocZeroed((nuint)sizeof(IGCodecCapability));
        _capability->StructSize = sizeof(IGCodecCapability);
        _capability->CodecId = MakeStringRef(CodecIdString);
        _capability->CodecName = MakeStringRef(CodecNameString);

        _capability->MetadataPriority = Priority;
        _capability->DecodePriority = Priority;
        _capability->EncodePriority = Priority;

        _capability->SupportsMetadata = 1;
        _capability->SupportsColorProfiles = 1;

        // An empty list is the host's catch-all convention, so a codec that discovered nothing
        // must claim nothing rather than claim everything.
        _capability->SupportsStaticRasterDecoding = decodeExtensions.Length > 0 ? 1 : 0;
        _capability->DecodeExtensionCount = decodeExtensions.Length;
        _capability->DecodeExtensions = MakeStringRefArray(decodeExtensions);

        // Page stacks only (TIFF, ICO, HEIF...): the host steps their frames through DecodeStaticRaster.
        _capability->SupportsAnimationDecoding = 0;

        _capability->SupportsStaticRasterEncoding = encodeExtensions.Length > 0 ? 1 : 0;
        _capability->SupportsMultiFrameEncoding = encodeExtensions.Length > 0 ? 1 : 0;
        _capability->EncodeExtensionCount = encodeExtensions.Length;
        _capability->EncodeExtensions = MakeStringRefArray(encodeExtensions);
    }


    private static void InitCodecApi()
    {
        _codecApi = (IGCodecApi*)NativeMemory.AllocZeroed((nuint)sizeof(IGCodecApi));
        _codecApi->StructSize = sizeof(IGCodecApi);
        _codecApi->GetCapability = &CodecGetCapability;
        _codecApi->CanHandleExtension = &CodecCanHandleExtension;

        // Left null on purpose: WIC already sniffs content when it opens a file, so a signature
        // probe here would just be a second, worse copy of that.
        _codecApi->CanHandleSignature = null;

        _codecApi->LoadMetadata = &CodecLoadMetadata;
        _codecApi->DecodeStaticRaster = &CodecDecodeStaticRaster;
        _codecApi->DecodeStaticRasterScaled = &CodecDecodeStaticRasterScaled;
        _codecApi->FreePixelBuffer = &CodecFreePixelBuffer;

        _codecApi->GetAnimationInfo = null;
        _codecApi->FreeAnimationInfo = null;
        _codecApi->DecodeAnimationFrame = null;

        _codecApi->EncodeStaticRaster = &CodecEncodeStaticRaster;
        _codecApi->BeginEncodeMultiFrame = &CodecBeginEncodeMultiFrame;
        _codecApi->EncodeFrame = &CodecEncodeFrame;
        _codecApi->EndEncodeMultiFrame = &CodecEndEncodeMultiFrame;
    }


    /// <summary>
    /// Builds the GIF/WebP codec, or leaves it null when no animated container was discovered.
    /// </summary>
    private static void InitAnimationCodec()
    {
        var extensions = WicFormats.AnimationExtensions;

        // An empty extension list is the host's catch-all, so with nothing to claim the codec is not offered at all.
        if (extensions.Length == 0) return;

        _animCapability = (IGCodecCapability*)NativeMemory.AllocZeroed((nuint)sizeof(IGCodecCapability));
        _animCapability->StructSize = sizeof(IGCodecCapability);
        _animCapability->CodecId = MakeStringRef(AnimCodecIdString);
        _animCapability->CodecName = MakeStringRef(AnimCodecNameString);

        _animCapability->MetadataPriority = Priority;
        _animCapability->DecodePriority = Priority;
        _animCapability->EncodePriority = Priority;

        _animCapability->SupportsMetadata = 1;
        _animCapability->SupportsColorProfiles = 1;
        _animCapability->SupportsStaticRasterDecoding = 1;
        _animCapability->SupportsAnimationDecoding = 1;
        _animCapability->DecodeExtensionCount = extensions.Length;
        _animCapability->DecodeExtensions = MakeStringRefArray(extensions);

        // Writing GIF stays with the main codec, whose encoder list already covers it.
        _animCapability->SupportsStaticRasterEncoding = 0;
        _animCapability->SupportsMultiFrameEncoding = 0;

        _animCodecApi = (IGCodecApi*)NativeMemory.AllocZeroed((nuint)sizeof(IGCodecApi));
        _animCodecApi->StructSize = sizeof(IGCodecApi);
        _animCodecApi->GetCapability = &AnimGetCapability;
        _animCodecApi->CanHandleExtension = &AnimCanHandleExtension;
        _animCodecApi->CanHandleSignature = null;
        _animCodecApi->LoadMetadata = &AnimLoadMetadata;
        _animCodecApi->DecodeStaticRaster = &AnimDecodeStaticRaster;
        _animCodecApi->DecodeStaticRasterScaled = &AnimDecodeStaticRasterScaled;
        _animCodecApi->FreePixelBuffer = &CodecFreePixelBuffer;
        _animCodecApi->GetAnimationInfo = &AnimGetAnimationInfo;
        _animCodecApi->FreeAnimationInfo = &AnimFreeAnimationInfo;
        _animCodecApi->DecodeAnimationFrame = &AnimDecodeAnimationFrame;
    }


    private static void InitPluginApi()
    {
        _pluginApi = (IGPluginApi*)NativeMemory.AllocZeroed((nuint)sizeof(IGPluginApi));
        _pluginApi->StructSize = sizeof(IGPluginApi);
        _pluginApi->AbiVersion = IGNativeAbi.IG_PLUGIN_ABI_VERSION;
        _pluginApi->Info = new IGPluginInfo
        {
            PluginId = MakeStringRef(PluginIdString),
            Name = MakeStringRef(PluginNameString),
            Version = MakeStringRef(VersionString),
            AbiVersion = IGNativeAbi.IG_PLUGIN_ABI_VERSION,
            CodecCount = _animCodecApi != null ? 2 : 1,
        };
        _pluginApi->GetCodec = &OnGetCodec;
        _pluginApi->Initialize = &OnInitialize;
        _pluginApi->Shutdown = &OnShutdown;
        _pluginApi->SelfTest = null;
    }


    // ------------------------------ Helpers ------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IGStringRef MakeStringRef(string value)
        => new() { Data = NativeBuffers.AllocUtf16(value), Length = value.Length };


    private static IGStringRef* MakeStringRefArray(string[] values)
    {
        if (values.Length == 0) return null;

        var array = (IGStringRef*)NativeMemory.AllocZeroed((nuint)(sizeof(IGStringRef) * values.Length));
        for (var i = 0; i < values.Length; i++)
        {
            array[i] = MakeStringRef(values[i]);
        }
        return array;
    }


    private static bool TryGetPath(IGStringRef value, out string path)
    {
        if (value.Data == null || value.Length <= 0)
        {
            path = string.Empty;
            return false;
        }

        path = new string(value.Data, 0, value.Length);
        return true;
    }
}
