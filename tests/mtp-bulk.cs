using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using CrispyHarmonyPhotoImport;

static class BulkTests
{
    static PropertyKey Key(uint id) { return new PropertyKey("ef6b490d-5cd8-437a-affc-da8b60ee4a3c", id); }
    static T Create<T>(string clsid) { return (T)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid))); }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    static IPortableDeviceValues Item(string id, string name, long? size)
    {
        var values = Create<IPortableDeviceValues>("0c15d503-d017-47ce-9016-7b3f978721cc");
        var key = Key(2);
        values.SetStringValue(ref key, id);
        if (name != null) { key = Key(4); values.SetStringValue(ref key, name); }
        if (size.HasValue)
        {
            key = Key(11);
            var value = new PropVariant { Type = (ushort)(size.Value < 0 ? VarEnum.VT_ERROR : VarEnum.VT_UI8), FileTime = size.Value };
            values.SetValue(ref key, ref value);
        }
        return values;
    }

    sealed class Properties : IPortableDeviceProperties, IPortableDevicePropertiesBulk
    {
        internal int BulkCalls, SingleCalls;
        internal bool WasMta, FailBulk;
        internal bool ReturnNoMedia;
        IPortableDevicePropertiesBulkCallback callback;
        List<string> ids;
        public void GetSupportedProperties() { throw new NotImplementedException(); }
        public void GetPropertyAttributes() { throw new NotImplementedException(); }
        public void GetValues(string id, IPortableDeviceKeyCollection keys, out IPortableDeviceValues values)
        {
            SingleCalls++;
            values = Item(id, ReturnNoMedia ? ".nomedia" : "file-" + id, 4096);
            if (ReturnNoMedia) { var key = Key(12); values.SetStringValue(ref key, ".nomedia"); }
        }
        public void QueueGetValuesByObjectList(IPortableDevicePropVariantCollection objects, IPortableDeviceKeyCollection keys,
            IPortableDevicePropertiesBulkCallback handler, out Guid context)
        {
            BulkCalls++;
            WasMta = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA;
            Check(WasMta, "Bulk callback was created on an STA thread");
            if (FailBulk) throw new COMException("Unsupported bulk", unchecked((int)0x80004001));
            callback = handler;
            context = Guid.NewGuid();
            uint count;
            objects.GetCount(out count);
            ids = new List<string>();
            for (uint i = 0; i < count; i++)
            {
                PropVariant value;
                objects.GetAt(i, out value);
                try { ids.Add(Marshal.PtrToStringUni(value.Pointer)); }
                finally { PropVariant.PropVariantClear(ref value); }
            }
        }
        public void QueueGetValuesByObjectFormat() { throw new NotImplementedException(); }
        public void QueueSetValuesByObjectList() { throw new NotImplementedException(); }
        public int Cancel(ref Guid context) { return 0; }

        void Send(ref Guid context, bool names, bool errors = false)
        {
            var collection = Create<IPortableDeviceValuesCollection>("3882134d-14cf-4220-9cb4-435f86d83f60");
            try
            {
                // Leave one object missing to verify the individual-read fallback.
                for (int i = 0; i < ids.Count - 1; i++)
                {
                    var item = Item(ids[i], names ? "file-" + ids[i] : null, names ? (long?)null : errors ? -1 : 4096);
                    try { collection.Add(item); }
                    finally { Marshal.ReleaseComObject(item); }
                }
                var pointer = Marshal.GetIUnknownForObject(collection);
                try { Check(callback.OnProgress(ref context, pointer) == 0, "Callback failed"); }
                finally { Marshal.Release(pointer); }
            }
            finally { Marshal.ReleaseComObject(collection); }
        }

        public void Start(ref Guid context)
        {
            callback.OnStart(ref context);
            Send(ref context, true);
            Send(ref context, false);
            Send(ref context, false, true);
            callback.OnEnd(ref context, 0);
        }
    }

    [STAThread]
    static void Main()
    {
        var ids = new List<string>();
        for (int i = 0; i < 10; i++) ids.Add(i.ToString());
        var keys = Create<IPortableDeviceKeyCollection>("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
        try
        {
            var read = typeof(MtpDevice).GetMethod("ReadValues", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (bool failBulk in new[] { false, true })
            {
                var properties = new Properties { FailBulk = failBulk };
                var device = (MtpDevice)FormatterServices.GetUninitializedObject(typeof(MtpDevice));
                foreach (var name in new[] { "properties", "bulk" })
                    typeof(MtpDevice).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(device, properties);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var result = (Dictionary<string, IPortableDeviceValues>)read.Invoke(device, new object[] { ids, keys, false });
                    try
                    {
                        Check(result.Count == 10, "Objects were omitted or repeated");
                        foreach (var id in ids)
                        {
                            var nameKey = Key(4);
                            string name;
                            Check(result[id].GetStringValue(ref nameKey, out name) == 0 && name == "file-" + id, "Name merge");
                            var sizeKey = Key(11);
                            ulong size;
                            Check(result[id].GetUnsignedLargeIntegerValue(ref sizeKey, out size) == 0 && size == 4096, "Size merge");
                        }
                    }
                    finally { foreach (var values in result.Values) Marshal.ReleaseComObject(values); }
                }
                Check(properties.WasMta, "MTA worker was not used");
                Check(properties.BulkCalls == (failBulk ? 1 : 2), "Unsupported bulk must be disabled");
                Check(properties.SingleCalls == (failBulk ? 20 : 2), "Missing-object fallback count");
            }
            var noMediaProperties = new Properties { ReturnNoMedia = true };
            var noMediaDevice = (MtpDevice)FormatterServices.GetUninitializedObject(typeof(MtpDevice));
            typeof(MtpDevice).GetField("properties", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(noMediaDevice, noMediaProperties);
            var manyIds = new List<string>();
            for (int i = 0; i < 600; i++) manyIds.Add(i.ToString());
            var stopped = (Dictionary<string, IPortableDeviceValues>)read.Invoke(noMediaDevice, new object[] { manyIds, keys, true });
            try { Check(stopped.Count == 256 && noMediaProperties.SingleCalls == 256, ".nomedia must stop further batches"); }
            finally { foreach (var values in stopped.Values) Marshal.ReleaseComObject(values); }
            using (var canceled = new MtpDevice.BulkValuesCallback())
            {
                var context = Guid.NewGuid();
                Check(canceled.OnProgress(ref context, IntPtr.Zero) == 0, "Empty progress packet was rejected");
                canceled.Dispose();
                Check(canceled.OnProgress(ref context, IntPtr.Zero) == unchecked((int)0x80004004), "Canceled callback was accepted");
            }
            Console.WriteLine("Bulk MTA dispatch, fragmented results, missing objects and unsupported drivers verified.");
        }
        finally { Marshal.ReleaseComObject(keys); }
    }
}
