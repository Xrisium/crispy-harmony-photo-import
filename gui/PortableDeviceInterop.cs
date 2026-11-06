using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace CrispyHarmonyPhotoImport
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        internal Guid Format;
        internal uint Id;

        internal PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
    }

    // WPD IUnknown vtables from PortableDeviceApi.h and PortableDeviceTypes.h.
    // Unused slots are declared only to preserve the positions of the methods we call.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40")]
    internal interface IPortableDeviceManager
    {
        [PreserveSig] int GetDevices([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] ids, ref uint count);
        void RefreshDeviceList();
        [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string id,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, ref uint length);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c")]
    internal interface IPortableDevice
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceValues client);
        void SendCommand();
        void Content(out IPortableDeviceContent content);
        void Capabilities();
        void Cancel();
        [PreserveSig] int Close();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6a96ed84-7c73-4480-9938-bf5af477d426")]
    internal interface IPortableDeviceContent
    {
        void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parent, IPortableDeviceValues filter,
            out IEnumPortableDeviceObjectIds objects);
        void Properties(out IPortableDeviceProperties properties);
        void Transfer(out IPortableDeviceResources resources);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19")]
    internal interface IEnumPortableDeviceObjectIds
    {
        [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] ids, out uint fetched);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("7f6d695c-03df-4439-a809-59266beee3a6")]
    internal interface IPortableDeviceProperties
    {
        void GetSupportedProperties();
        void GetPropertyAttributes();
        void GetValues([MarshalAs(UnmanagedType.LPWStr)] string id, IPortableDeviceKeyCollection keys,
            out IPortableDeviceValues values);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("fd8878ac-d841-4d17-891c-e6829cdb6934")]
    internal interface IPortableDeviceResources
    {
        void GetSupportedResources();
        void GetResourceAttributes();
        void GetStream([MarshalAs(UnmanagedType.LPWStr)] string id, ref PropertyKey key, uint mode,
            ref uint optimalBufferSize, out IStream stream);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("dada2357-e0ad-492e-98db-dd61c53ba353")]
    internal interface IPortableDeviceKeyCollection
    {
        void GetCount();
        void GetAt();
        void Add(ref PropertyKey key);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143")]
    internal interface IPortableDeviceValues
    {
        void GetCount();
        void GetAt();
        void SetValue();
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value);
        void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
        void GetUnsignedIntegerValue();
        void SetSignedIntegerValue();
        void GetSignedIntegerValue();
        void SetUnsignedLargeIntegerValue();
        [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
        void SetSignedLargeIntegerValue();
        void GetSignedLargeIntegerValue();
        void SetFloatValue();
        void GetFloatValue();
        void SetErrorValue();
        void GetErrorValue();
        void SetKeyValue();
        void GetKeyValue();
        void SetBoolValue();
        [PreserveSig] int GetBoolValue(ref PropertyKey key, out int value);
        void SetIUnknownValue();
        void GetIUnknownValue();
        void SetGuidValue();
        [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal long FileTime;
        [FieldOffset(8)] internal double Date;

        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PropVariant value);
    }
}
