using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Shike;

public sealed record DevicePosition(double Latitude, double Longitude, double Accuracy, DateTime Timestamp)
{
    public void Validate()
    {
        if (!double.IsFinite(Latitude) || !double.IsFinite(Longitude) || !double.IsFinite(Accuracy) || Latitude < -90 || Latitude > 90 || Longitude < -180 || Longitude > 180 || Accuracy <= 0 || Accuracy > 200 || Math.Abs((DateTime.UtcNow - Timestamp.ToUniversalTime()).TotalSeconds) > 120)
            throw new InvalidDataException("本机定位精度不足或已过期，请在学在浙大完成签到。");
    }
}

public static class DeviceLocation
{
    // ABI matches Windows SDK locationapi.h; coordinates are never persisted.
    [ComImport, Guid("AB2ECE69-56D9-4F28-B525-DE1B0EE44237"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ILocation
    {
        [PreserveSig] int RegisterForReport(IntPtr events, ref Guid type, uint interval);
        [PreserveSig] int UnregisterForReport(ref Guid type);
        [PreserveSig] int GetReport(ref Guid type, [MarshalAs(UnmanagedType.Interface)] out object report);
        [PreserveSig] int GetReportStatus(ref Guid type, out int status);
        [PreserveSig] int GetReportInterval(ref Guid type, out uint interval);
        [PreserveSig] int SetReportInterval(ref Guid type, uint interval);
        [PreserveSig] int GetDesiredAccuracy(ref Guid type, out int accuracy);
        [PreserveSig] int SetDesiredAccuracy(ref Guid type, int accuracy);
        [PreserveSig] int RequestPermissions(IntPtr parent, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] Guid[] types, uint count, [MarshalAs(UnmanagedType.Bool)] bool modal);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct SystemTime { public ushort Year, Month, Weekday, Day, Hour, Minute, Second, Milliseconds; }
    [ComImport, Guid("7FED806D-0EF8-4F07-80AC-36A0BEAE3134"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ILatLongReport
    {
        [PreserveSig] int GetSensorID(out Guid sensor);
        [PreserveSig] int GetTimestamp(out SystemTime time);
        [PreserveSig] int GetValue(IntPtr key, IntPtr value);
        [PreserveSig] int GetLatitude(out double latitude);
        [PreserveSig] int GetLongitude(out double longitude);
        [PreserveSig] int GetAltitude(out double altitude);
        [PreserveSig] int GetErrorRadius(out double radius);
        [PreserveSig] int GetAltitudeError(out double error);
    }
    public static async Task<DevicePosition> Read(IntPtr owner, bool requestPermission, CancellationToken ct)
    {
        object? instance = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("E5B8E079-EE6D-4E33-A438-C87F2E959254"), true)!);
            var location = (ILocation)instance!; var iid = typeof(ILatLongReport).GUID;
            if (requestPermission) Marshal.ThrowExceptionForHR(location.RequestPermissions(owner, new[] { iid }, 1, true));
            Marshal.ThrowExceptionForHR(location.SetDesiredAccuracy(ref iid, 1));
            for (int n = 0; n < 15; n++)
            {
                ct.ThrowIfCancellationRequested();
                int hr = location.GetReport(ref iid, out var obj);
                if (hr >= 0 && obj != null)
                {
                    try
                    {
                        var report = (ILatLongReport)obj;
                        Marshal.ThrowExceptionForHR(report.GetLatitude(out var lat)); Marshal.ThrowExceptionForHR(report.GetLongitude(out var lon));
                        Marshal.ThrowExceptionForHR(report.GetErrorRadius(out var radius)); Marshal.ThrowExceptionForHR(report.GetTimestamp(out var t));
                        var position = new DevicePosition(lat, lon, radius, new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, t.Milliseconds, DateTimeKind.Utc));
                        position.Validate(); return position;
                    }
                    finally { Marshal.ReleaseComObject(obj); }
                }
                if (hr == unchecked((int)0x80070005)) throw new InvalidDataException("Windows 未允许定位，请在系统隐私设置中开启定位，或在学在浙大完成签到。");
                await Task.Delay(1000, ct);
            }
            throw new InvalidDataException("未取得本机实时位置，请检查 Windows 定位服务或在学在浙大完成签到。");
        }
        catch (COMException) { throw new InvalidDataException("此电脑的 Windows 定位服务不可用，请在学在浙大完成签到。"); }
        finally { if (instance != null) Marshal.ReleaseComObject(instance); }
    }
}
