using Avalonia.Controls;
using Avalonia.Platform;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VideoAnalysis.App.Media;

public sealed class MacAvFoundationVideoView : NativeControlHost, IDisposable
{
    private IntPtr _nativeView;
    private IntPtr _playerLayer;
    private IntPtr _player;
    private bool _isMuted;
    private int _volume = 100;
    private double _rate = 1d;
    private bool _disposed;

    public bool IsPlaying { get; private set; }

    public double CurrentSeconds => MacObjectiveC.TryGetPlayerSeconds(_player, "currentTime");

    public double DurationSeconds
    {
        get
        {
            var item = MacObjectiveC.SendIntPtr(_player, "currentItem");
            return MacObjectiveC.TryGetPlayerItemSeconds(item, "duration");
        }
    }

    public void Open(string source, bool startPlaying)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("AVFoundation playback is only available on macOS.");
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Video source is required.", nameof(source));
        }

        EnsureNativePlayer();
        var url = MacObjectiveC.CreateUrl(source);
        if (url == IntPtr.Zero)
        {
            throw new InvalidOperationException($"AVFoundation could not create an NSURL for '{source}'.");
        }

        var player = MacObjectiveC.AllocInitWithIntPtr("AVPlayer", "initWithURL:", url);
        if (player == IntPtr.Zero)
        {
            throw new InvalidOperationException($"AVFoundation could not create AVPlayer for '{source}'.");
        }

        MacObjectiveC.SendRetain(player);
        ReplacePlayer(player);
        ApplyVolume();
        SetPlaybackRate(_rate);

        if (startPlaying)
        {
            Play();
        }
        else
        {
            Pause();
        }
    }

    public void Play()
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        MacObjectiveC.SendVoid(_player, "play");
        if (_rate > 0)
        {
            MacObjectiveC.SendVoidFloat(_player, "setRate:", (float)_rate);
        }

        IsPlaying = true;
    }

    public void Pause()
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        MacObjectiveC.SendVoid(_player, "pause");
        IsPlaying = false;
    }

    public void Seek(double seconds)
    {
        if (_player == IntPtr.Zero || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return;
        }

        var time = MacObjectiveC.CMTimeMakeWithSeconds(Math.Max(0d, seconds), 600);
        MacObjectiveC.SendVoidCMTime(_player, "seekToTime:", time);
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        ApplyVolume();
    }

    public void SetMuted(bool muted)
    {
        _isMuted = muted;
        ApplyVolume();
    }

    public void SetPlaybackRate(double rate)
    {
        _rate = rate <= 0 ? 1d : rate;
        if (_player != IntPtr.Zero && IsPlaying)
        {
            MacObjectiveC.SendVoidFloat(_player, "setRate:", (float)_rate);
        }
    }

    public void ClosePlayer()
    {
        Pause();
        ReplacePlayer(IntPtr.Zero);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return base.CreateNativeControlCore(parent);
        }

        EnsureNativePlayer();
        return new PlatformHandle(_nativeView, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsMacOS())
        {
            ClosePlayer();
            ReleaseNative(ref _playerLayer);
            ReleaseNative(ref _nativeView);
        }

        base.DestroyNativeControlCore(control);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClosePlayer();
        ReleaseNative(ref _playerLayer);
        ReleaseNative(ref _nativeView);
    }

    private void EnsureNativePlayer()
    {
        if (_nativeView != IntPtr.Zero)
        {
            return;
        }

        _nativeView = MacObjectiveC.CreateLayerBackedView();
        _playerLayer = MacObjectiveC.CreateAvPlayerLayer();
        MacObjectiveC.SendVoidIntPtr(_nativeView, "setLayer:", _playerLayer);
        MacObjectiveC.SendRetain(_nativeView);
        MacObjectiveC.SendRetain(_playerLayer);
    }

    private void ReplacePlayer(IntPtr player)
    {
        if (_playerLayer != IntPtr.Zero)
        {
            MacObjectiveC.SendVoidIntPtr(_playerLayer, "setPlayer:", player);
        }

        ReleaseNative(ref _player);
        _player = player;
    }

    private void ApplyVolume()
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        MacObjectiveC.SendVoidBool(_player, "setMuted:", _isMuted);
        MacObjectiveC.SendVoidFloat(_player, "setVolume:", _volume / 100f);
    }

    private static void ReleaseNative(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            MacObjectiveC.SendVoid(handle, "release");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"AVFoundation native release failed: {ex}");
        }
        finally
        {
            handle = IntPtr.Zero;
        }
    }
}

internal static class MacObjectiveC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string AvFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    private const string QuartzCore = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private static bool _frameworksLoaded;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CMTime
    {
        public long Value;
        public int Timescale;
        public uint Flags;
        public long Epoch;
    }

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_CGRect(IntPtr receiver, IntPtr selector, CGRect value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_bool(IntPtr receiver, IntPtr selector, byte value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_float(IntPtr receiver, IntPtr selector, float value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_CMTime(IntPtr receiver, IntPtr selector, CMTime value);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern CMTime objc_msgSend_CMTime(IntPtr receiver, IntPtr selector);

    [DllImport(CoreMedia)]
    internal static extern CMTime CMTimeMakeWithSeconds(double seconds, int preferredTimescale);

    [DllImport(CoreMedia)]
    private static extern double CMTimeGetSeconds(CMTime time);

    internal static IntPtr CreateLayerBackedView()
    {
        EnsureFrameworksLoaded();
        var view = AllocInitWithCGRect("NSView", "initWithFrame:", default);
        SendVoidBool(view, "setWantsLayer:", true);
        return view;
    }

    internal static IntPtr CreateAvPlayerLayer()
    {
        EnsureFrameworksLoaded();
        var layer = AllocInit("AVPlayerLayer");
        var gravity = CreateNSString("AVLayerVideoGravityResizeAspect");
        SendVoidIntPtr(layer, "setVideoGravity:", gravity);
        return layer;
    }

    internal static IntPtr CreateUrl(string source)
    {
        EnsureFrameworksLoaded();
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            var urlString = CreateNSString(source);
            return SendIntPtr(GetClass("NSURL"), "URLWithString:", urlString);
        }

        var path = Uri.TryCreate(source, UriKind.Absolute, out uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
                ? uri.LocalPath
                : source;
        var nsPath = CreateNSString(Path.GetFullPath(path));
        return SendIntPtr(GetClass("NSURL"), "fileURLWithPath:", nsPath);
    }

    internal static IntPtr AllocInit(string className)
    {
        return SendIntPtr(SendIntPtr(GetClass(className), "alloc"), "init");
    }

    internal static IntPtr AllocInitWithIntPtr(string className, string initSelector, IntPtr value)
    {
        return SendIntPtr(SendIntPtr(GetClass(className), "alloc"), initSelector, value);
    }

    internal static IntPtr AllocInitWithCGRect(string className, string initSelector, CGRect value)
    {
        return objc_msgSend_CGRect(SendIntPtr(GetClass(className), "alloc"), GetSelector(initSelector), value);
    }

    internal static IntPtr SendIntPtr(IntPtr receiver, string selector)
    {
        return receiver == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(receiver, GetSelector(selector));
    }

    internal static IntPtr SendIntPtr(IntPtr receiver, string selector, IntPtr value)
    {
        return receiver == IntPtr.Zero ? IntPtr.Zero : objc_msgSend_IntPtr(receiver, GetSelector(selector), value);
    }

    internal static void SendVoid(IntPtr receiver, string selector)
    {
        if (receiver != IntPtr.Zero)
        {
            objc_msgSend_void(receiver, GetSelector(selector));
        }
    }

    internal static void SendVoidIntPtr(IntPtr receiver, string selector, IntPtr value)
    {
        if (receiver != IntPtr.Zero)
        {
            objc_msgSend_void_IntPtr(receiver, GetSelector(selector), value);
        }
    }

    internal static void SendVoidBool(IntPtr receiver, string selector, bool value)
    {
        if (receiver != IntPtr.Zero)
        {
            objc_msgSend_void_bool(receiver, GetSelector(selector), value ? (byte)1 : (byte)0);
        }
    }

    internal static void SendVoidFloat(IntPtr receiver, string selector, float value)
    {
        if (receiver != IntPtr.Zero)
        {
            objc_msgSend_void_float(receiver, GetSelector(selector), value);
        }
    }

    internal static void SendVoidCMTime(IntPtr receiver, string selector, CMTime value)
    {
        if (receiver != IntPtr.Zero)
        {
            objc_msgSend_void_CMTime(receiver, GetSelector(selector), value);
        }
    }

    internal static void SendRetain(IntPtr receiver)
    {
        SendVoid(receiver, "retain");
    }

    internal static double TryGetPlayerSeconds(IntPtr player, string selector)
    {
        return TryGetSeconds(player, selector);
    }

    internal static double TryGetPlayerItemSeconds(IntPtr item, string selector)
    {
        return TryGetSeconds(item, selector);
    }

    private static double TryGetSeconds(IntPtr receiver, string selector)
    {
        if (receiver == IntPtr.Zero)
        {
            return 0d;
        }

        try
        {
            var time = objc_msgSend_CMTime(receiver, GetSelector(selector));
            var seconds = CMTimeGetSeconds(time);
            return double.IsNaN(seconds) || double.IsInfinity(seconds) ? 0d : Math.Max(0d, seconds);
        }
        catch
        {
            return 0d;
        }
    }

    private static IntPtr CreateNSString(string value)
    {
        var nsString = Alloc(GetClass("NSString"));
        return objc_msgSend_String(nsString, GetSelector("initWithUTF8String:"), value);
    }

    private static IntPtr Alloc(IntPtr classHandle)
    {
        return SendIntPtr(classHandle, "alloc");
    }

    private static IntPtr GetClass(string name)
    {
        EnsureFrameworksLoaded();
        var handle = objc_getClass(name);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Objective-C class '{name}' was not found.");
        }

        return handle;
    }

    private static IntPtr GetSelector(string name)
    {
        return sel_registerName(name);
    }

    private static void EnsureFrameworksLoaded()
    {
        if (_frameworksLoaded)
        {
            return;
        }

        NativeLibrary.Load(AvFoundation);
        NativeLibrary.Load(QuartzCore);
        NativeLibrary.Load(CoreMedia);
        _frameworksLoaded = true;
    }

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_String(
        IntPtr receiver,
        IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
}
