using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VideoAnalysis.App.Media;

#pragma warning disable CA1416

public static class WindowsCameraDeviceEnumerator
{
    private static readonly Guid SystemDeviceEnumClassId = new("62BE5D10-60EB-11D0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputDeviceCategoryId = new("860BB310-5D01-11D0-BD3B-00A0C911CE86");
    private static readonly Guid PropertyBagInterfaceId = new("55272A00-42CB-11CE-8135-00AA004BB851");

    public static IReadOnlyList<CameraDeviceInfo> GetVideoCaptureDevices()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return [];
        }

        object? deviceEnumeratorObject = null;
        IEnumMoniker? monikerEnumerator = null;
        var cameras = new List<CameraDeviceInfo>();

        try
        {
            var deviceEnumeratorType = Type.GetTypeFromCLSID(SystemDeviceEnumClassId, throwOnError: true)
                ?? throw new InvalidOperationException("DirectShow device enumerator is unavailable.");
            deviceEnumeratorObject = Activator.CreateInstance(deviceEnumeratorType);
            if (deviceEnumeratorObject is not ICreateDevEnum deviceEnumerator)
            {
                return cameras;
            }

            var categoryId = VideoInputDeviceCategoryId;
            var result = deviceEnumerator.CreateClassEnumerator(ref categoryId, out monikerEnumerator, 0);
            if (result != 0 || monikerEnumerator is null)
            {
                return cameras;
            }

            var monikers = new IMoniker[1];
            while (monikerEnumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                if (TryReadFriendlyName(moniker, out var friendlyName)
                    && !string.IsNullOrWhiteSpace(friendlyName)
                    && cameras.All((camera) => !string.Equals(camera.Name, friendlyName, StringComparison.OrdinalIgnoreCase)))
                {
                    cameras.Add(new CameraDeviceInfo(friendlyName));
                }

                Marshal.ReleaseComObject(moniker);
                monikers[0] = null!;
            }
        }
        finally
        {
            if (monikerEnumerator is not null)
            {
                Marshal.ReleaseComObject(monikerEnumerator);
            }

            if (deviceEnumeratorObject is not null)
            {
                Marshal.ReleaseComObject(deviceEnumeratorObject);
            }
        }

        return cameras;
    }

    private static bool TryReadFriendlyName(IMoniker moniker, out string friendlyName)
    {
        friendlyName = string.Empty;
        object? propertyBagObject = null;

        try
        {
            var propertyBagId = PropertyBagInterfaceId;
            moniker.BindToStorage(null!, null!, ref propertyBagId, out propertyBagObject);
            if (propertyBagObject is not IPropertyBag propertyBag)
            {
                return false;
            }

            var result = propertyBag.Read("FriendlyName", out var value, IntPtr.Zero);
            friendlyName = value as string ?? string.Empty;
            return result == 0;
        }
        catch
        {
            friendlyName = string.Empty;
            return false;
        }
        finally
        {
            if (propertyBagObject is not null)
            {
                Marshal.ReleaseComObject(propertyBagObject);
            }
        }
    }

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(
            [In] ref Guid clsidDeviceClass,
            out IEnumMoniker? enumMoniker,
            int flags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] out object value,
            IntPtr errorLog);

        [PreserveSig]
        int Write(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}

#pragma warning restore CA1416
