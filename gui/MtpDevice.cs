using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CrispyHarmonyPhotoImport
{
    // Loaded by the import script from this EXE. All device handles live in that process.
    public sealed class MtpDevice : IDisposable
    {
        private const string ObjectProperties = "ef6b490d-5cd8-437a-affc-da8b60ee4a3c";
        private const string ClientProperties = "204d9f0c-2292-4080-9f42-40664e70f859";
        private const string Root = "DEVICE";
        private static readonly PropertyKey ObjectId = new PropertyKey(ObjectProperties, 2);
        private static readonly PropertyKey ObjectName = new PropertyKey(ObjectProperties, 4);
        private static readonly PropertyKey ContentType = new PropertyKey(ObjectProperties, 7);
        private static readonly PropertyKey IsHidden = new PropertyKey(ObjectProperties, 9);
        private static readonly PropertyKey ObjectSize = new PropertyKey(ObjectProperties, 11);
        private static readonly PropertyKey OriginalName = new PropertyKey(ObjectProperties, 12);
        private static readonly PropertyKey DateModified = new PropertyKey(ObjectProperties, 19);
        private static readonly PropertyKey FunctionalCategory = new PropertyKey("8f052d93-abca-4fc5-a5ac-b01df4dbe598", 2);
        private static readonly PropertyKey DefaultResource = new PropertyKey("e81e79be-34f0-41bf-b53f-f1a06ae87842", 0);
        private static readonly Guid StorageType = new Guid("23f05bbc-15de-4c2a-a55b-a9af5ce412ef");
        private static readonly Guid FolderType = new Guid("27e2e392-a111-48e0-ab0c-e17705a05f85");
        private static readonly Guid ImageType = new Guid("ef2107d5-a52a-4243-a26b-62d4176d7603");
        private static readonly Guid VideoType = new Guid("9261b03c-3d78-4519-85e3-02c5e1f50bb9");
        // Some MTP implementations report newer formats as generic files.
        private static readonly HashSet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".hif", ".avif",
            ".jxl", ".dng", ".raw", ".arw", ".cr2", ".cr3", ".nef", ".raf", ".rw2", ".orf", ".tif", ".tiff",
            ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".mkv", ".webm", ".avi", ".mpg", ".mpeg", ".mts", ".m2ts",
            ".ts", ".wmv", ".flv"
        };

        private IPortableDevice device;
        private IPortableDeviceContent content;
        private IPortableDeviceProperties properties;
        private IPortableDeviceResources resources;
        private IPortableDeviceKeyCollection keys;
        private IPortableDeviceKeyCollection storageKeys, nameKeys, visibilityKeys, sizeKeys, importKeys;
        private IPortableDevicePropertiesBulk bulk;
        private bool opened;

        public bool IncludeModified { get; set; }
        public bool RespectNoMedia { get; set; }

        public sealed class Phone
        {
            public string Id { get; internal set; }
            public string Name { get; internal set; }
        }

        public sealed class Entry
        {
            public string Id { get; internal set; }
            public string Name { get; internal set; }
            public bool IsFolder { get; internal set; }
            public long? Size { get; internal set; }
            public string Modified { get; internal set; }
        }

        public sealed class Storage
        {
            public string Id { get; internal set; }
            public string Name { get; internal set; }
        }

        public static Phone[] ListPhones()
        {
            var manager = Create<IPortableDeviceManager>("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
            try
            {
                manager.RefreshDeviceList();
                uint count = 0;
                Marshal.ThrowExceptionForHR(manager.GetDevices(null, ref count));
                var phones = new List<Phone>();
                if (count == 0) return phones.ToArray();

                var ids = new IntPtr[count];
                try
                {
                    Marshal.ThrowExceptionForHR(manager.GetDevices(ids, ref count));
                    for (var i = 0; i < count; i++)
                    {
                        var id = Marshal.PtrToStringUni(ids[i]);
                        // WPD also enumerates network devices and local card readers.
                        if (id == null || !id.StartsWith(@"\\?\usb#", StringComparison.OrdinalIgnoreCase)) continue;
                        uint length = 0;
                        string name = null;
                        manager.GetDeviceFriendlyName(id, null, ref length);
                        if (length > 0 && length < 4096)
                        {
                            var buffer = new StringBuilder((int)length);
                            if (manager.GetDeviceFriendlyName(id, buffer, ref length) >= 0) name = buffer.ToString();
                        }
                        phones.Add(new Phone { Id = id, Name = String.IsNullOrWhiteSpace(name) ? "USB 设备" : name });
                    }
                }
                finally { foreach (var id in ids) Marshal.FreeCoTaskMem(id); }
                return phones.ToArray();
            }
            finally { Release(manager); }
        }

        public MtpDevice(string id)
        {
            IncludeModified = true;
            Task.Run(() => Open(id)).GetAwaiter().GetResult();
        }

        private void Open(string id)
        {
            try
            {
                device = Create<IPortableDevice>("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
                var client = Create<IPortableDeviceValues>("0c15d503-d017-47ce-9016-7b3f978721cc");
                try
                {
                    var name = new PropertyKey(ClientProperties, 2);
                    var access = new PropertyKey(ClientProperties, 9);
                    client.SetStringValue(ref name, "crispy-harmony-photo-import");
                    client.SetUnsignedIntegerValue(ref access, 0x80000000); // GENERIC_READ
                    device.Open(id, client);
                    opened = true;
                }
                finally { Release(client); }

                device.Content(out content);
                content.Properties(out properties);
                content.Transfer(out resources);
                bulk = properties as IPortableDevicePropertiesBulk;
                keys = CreateKeys(ObjectId, OriginalName, ContentType);
                storageKeys = CreateKeys(ObjectName, FunctionalCategory);
                nameKeys = CreateKeys(ObjectName);
                visibilityKeys = CreateKeys(ObjectId, IsHidden);
                sizeKeys = CreateKeys(ObjectId, IsHidden, ObjectSize);
                importKeys = CreateKeys(ObjectId, IsHidden, ObjectSize, DateModified);
            }
            catch { DisposeCore(); throw; }
        }

        public Storage[] GetStorages()
        {
            return Task.Run(() => ReadStorages()).GetAwaiter().GetResult();
        }

        private Storage[] ReadStorages()
        {
            var storages = new List<Storage>();
            var storageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in GetChildren(Root))
            {
                IPortableDeviceValues values;
                properties.GetValues(id, storageKeys, out values);
                string storage;
                try
                {
                    if (ReadGuid(values, FunctionalCategory) != StorageType) continue;
                    storage = ReadString(values, ObjectName);
                }
                finally { Release(values); }
                if (String.IsNullOrWhiteSpace(storage)) throw new IOException("手机存储未提供可用的名称。");
                if (!storageNames.Add(storage)) throw new IOException("手机上有重名的存储卷：" + storage);
                storages.Add(new Storage { Id = id, Name = storage });
            }
            if (storageNames.Count == 0) throw new IOException("手机未开放存储。请解锁手机并选择 USB「文件传输」。");
            return storages.ToArray();
        }

        public Entry[] GetEntries(string parent, bool includeFiles = true)
        {
            return Task.Run(() => ReadEntries(parent, includeFiles)).GetAwaiter().GetResult();
        }

        private Entry[] ReadEntries(string parent, bool includeFiles)
        {
            var entries = new List<Entry>();
            List<string> children;
            try { children = GetChildren(parent); }
            catch (UnauthorizedAccessException) { return entries.ToArray(); }
            var identities = ReadValues(children, keys, RespectNoMedia);
            try
            {
                foreach (var pair in identities)
                {
                    var values = pair.Value;
                    var type = ReadGuid(values, ContentType);
                    var folder = type == FolderType;
                    var name = ReadString(values, OriginalName);
                    if (name == null)
                    {
                        IPortableDeviceValues fallback;
                        try { properties.GetValues(pair.Key, nameKeys, out fallback); }
                        catch (UnauthorizedAccessException) { continue; }
                        try { name = ReadString(fallback, ObjectName); }
                        finally { Release(fallback); }
                    }
                    if (RespectNoMedia && String.Equals(name, ".nomedia", StringComparison.OrdinalIgnoreCase)) return new Entry[0];
                    if (!folder && !includeFiles) continue;
                    if (name != null && name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (!folder && type != ImageType && type != VideoType)
                    {
                        var extension = name == null ? -1 : name.LastIndexOf('.');
                        if (extension < 0 || !MediaExtensions.Contains(name.Substring(extension))) continue;
                    }
                    if (String.IsNullOrEmpty(name)) throw new IOException("手机媒体未提供原始文件名。");
                    entries.Add(new Entry { Id = pair.Key, Name = name, IsFolder = folder });
                }
            }
            finally { foreach (var values in identities.Values) Release(values); }
            var mediaIds = new List<string>();
            var folderIds = new List<string>();
            foreach (var entry in entries)
            {
                if (entry.IsFolder) folderIds.Add(entry.Id);
                else mediaIds.Add(entry.Id);
            }
            var folders = ReadValues(folderIds, visibilityKeys);
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var pair in folders)
                {
                    var key = IsHidden;
                    int flag;
                    if (pair.Value.GetBoolValue(ref key, out flag) >= 0 && flag != 0) hidden.Add(pair.Key);
                }
            }
            finally { foreach (var values in folders.Values) Release(values); }
            var details = ReadValues(mediaIds, IncludeModified ? importKeys : sizeKeys);
            try
            {
                foreach (var entry in entries)
                {
                    IPortableDeviceValues values;
                    if (!details.TryGetValue(entry.Id, out values)) continue;
                    var hiddenKey = IsHidden;
                    int flag;
                    if (values.GetBoolValue(ref hiddenKey, out flag) >= 0 && flag != 0) { hidden.Add(entry.Id); continue; }
                    var key = ObjectSize;
                    ulong size;
                    if (values.GetUnsignedLargeIntegerValue(ref key, out size) >= 0 && size <= long.MaxValue)
                        entry.Size = (long)size;
                    if (IncludeModified) entry.Modified = ReadModified(values);
                }
            }
            finally { foreach (var values in details.Values) Release(values); }
            entries.RemoveAll(entry => hidden.Contains(entry.Id));
            return entries.ToArray();
        }

        private Dictionary<string, IPortableDeviceValues> ReadValues(List<string> ids, IPortableDeviceKeyCollection requested, bool stopAtNoMedia = false)
        {
            var result = new Dictionary<string, IPortableDeviceValues>(StringComparer.Ordinal);
            try
            {
                const int batchSize = 256;
                for (int offset = 0; offset < ids.Count; offset += batchSize)
                {
                    int count = Math.Min(batchSize, ids.Count - offset);
                    if (bulk != null && count >= 8)
                    {
                        try
                        {
                            // Create callbacks on an MTA thread even when PowerShell's caller is STA.
                            var batch = ids.GetRange(offset, count);
                            var values = Task.Run(() => ReadBulk(batch, requested)).GetAwaiter().GetResult();
                            foreach (var pair in values)
                            {
                                if (result.ContainsKey(pair.Key)) Release(pair.Value);
                                else result.Add(pair.Key, pair.Value);
                            }
                        }
                        catch (COMException) { bulk = null; }
                        catch (TimeoutException) { bulk = null; }
                    }
                    for (int i = offset; i < offset + count; i++)
                    {
                        if (result.ContainsKey(ids[i])) continue;
                        IPortableDeviceValues values;
                        try { properties.GetValues(ids[i], requested, out values); }
                        catch (UnauthorizedAccessException) { continue; }
                        result.Add(ids[i], values);
                    }
                    if (stopAtNoMedia)
                    {
                        for (int i = offset; i < offset + count; i++)
                        {
                            IPortableDeviceValues values;
                            if (result.TryGetValue(ids[i], out values) &&
                                String.Equals(ReadString(values, OriginalName), ".nomedia", StringComparison.OrdinalIgnoreCase)) return result;
                        }
                    }
                }
                return result;
            }
            catch
            {
                foreach (var values in result.Values) Release(values);
                throw;
            }
        }

        private Dictionary<string, IPortableDeviceValues> ReadBulk(List<string> ids, IPortableDeviceKeyCollection requested)
        {
            var objects = Create<IPortableDevicePropVariantCollection>("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9");
            try
            {
                using (var callback = new BulkValuesCallback())
                {
                    foreach (var id in ids)
                    {
                        var value = new PropVariant { Type = (ushort)VarEnum.VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(id) };
                        try { objects.Add(ref value); }
                        finally { PropVariant.PropVariantClear(ref value); }
                    }
                    Guid context;
                    bulk.QueueGetValuesByObjectList(objects, requested, callback, out context);
                    bulk.Start(ref context);
                    if (!callback.Wait()) { bulk.Cancel(ref context); throw new TimeoutException("批量读取手机属性超时。"); }
                    Marshal.ThrowExceptionForHR(callback.Status);
                    var result = new Dictionary<string, IPortableDeviceValues>(StringComparer.Ordinal);
                    try
                    {
                        foreach (var id in ids)
                        {
                            if (result.ContainsKey(id)) continue;
                            var values = callback.Take(id);
                            if (values == null) continue;
                            try
                            {
                                uint count;
                                values.GetCount(out count);
                                if (count <= 1) continue; // Missing properties are retried through GetValues.
                                result.Add(id, values);
                                values = null;
                            }
                            finally { Release(values); }
                        }
                        return result;
                    }
                    catch { foreach (var value in result.Values) Release(value); throw; }
                }
            }
            finally { Release(objects); }
        }

        private static IPortableDeviceKeyCollection CreateKeys(params PropertyKey[] properties)
        {
            var collection = Create<IPortableDeviceKeyCollection>("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
            try
            {
                foreach (var item in properties) { var key = item; collection.Add(ref key); }
                return collection;
            }
            catch { Release(collection); throw; }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class BulkValuesCallback : IPortableDevicePropertiesBulkCallback, IDisposable
        {
            private readonly Dictionary<string, IPortableDeviceValues> values = new Dictionary<string, IPortableDeviceValues>(StringComparer.Ordinal);
            private readonly TaskCompletionSource<int> completion = new TaskCompletionSource<int>();
            private int lastActivity = Environment.TickCount;
            private bool disposed;

            internal int Status { get { return completion.Task.Result; } }
            public int OnStart(ref Guid context) { return 0; }

            public int OnProgress(ref Guid context, IntPtr results)
            {
                lock (values)
                {
                    if (disposed) return unchecked((int)0x80004004);
                    if (results == IntPtr.Zero) return 0;
                    var collection = (IPortableDeviceValuesCollection)Marshal.GetObjectForIUnknown(results);
                    try
                    {
                        uint count;
                        collection.GetCount(out count);
                        for (uint i = 0; i < count; i++)
                        {
                            IPortableDeviceValues item;
                            collection.GetAt(i, out item);
                            try
                            {
                                var id = ReadString(item, ObjectId);
                                if (id == null) continue;
                                IPortableDeviceValues merged;
                                if (!values.TryGetValue(id, out merged))
                                {
                                    merged = Create<IPortableDeviceValues>("0c15d503-d017-47ce-9016-7b3f978721cc");
                                    values.Add(id, merged);
                                }
                                uint propertyCount;
                                item.GetCount(out propertyCount);
                                for (uint index = 0; index < propertyCount; index++)
                                {
                                    PropertyKey key;
                                    PropVariant value;
                                    item.GetAt(index, out key, out value);
                                    try
                                    {
                                        if (value.Type == (ushort)VarEnum.VT_ERROR)
                                        {
                                            PropVariant previous;
                                            var status = merged.GetValue(ref key, out previous);
                                            try { if (status >= 0 && previous.Type != (ushort)VarEnum.VT_ERROR) continue; }
                                            finally { PropVariant.PropVariantClear(ref previous); }
                                        }
                                        merged.SetValue(ref key, ref value);
                                    }
                                    finally { PropVariant.PropVariantClear(ref value); }
                                }
                            }
                            finally { Release(item); }
                        }
                        Interlocked.Exchange(ref lastActivity, Environment.TickCount);
                        return 0;
                    }
                    catch (Exception error) { return Marshal.GetHRForException(error); }
                    finally { Release(collection); }
                }
            }

            public int OnEnd(ref Guid context, int status) { completion.TrySetResult(status); return 0; }

            internal bool Wait()
            {
                while (!completion.Task.Wait(250))
                    if (unchecked(Environment.TickCount - Interlocked.CompareExchange(ref lastActivity, 0, 0)) > 30000) return false;
                return true;
            }

            internal IPortableDeviceValues Take(string id)
            {
                lock (values)
                {
                    IPortableDeviceValues item;
                    if (!values.TryGetValue(id, out item)) return null;
                    values.Remove(id);
                    return item;
                }
            }

            public void Dispose()
            {
                lock (values)
                {
                    disposed = true;
                    foreach (var item in values.Values) Release(item);
                    values.Clear();
                }
            }
        }

        private List<string> GetChildren(string parent)
        {
            var result = new List<string>();
            IEnumPortableDeviceObjectIds enumerator;
            content.EnumObjects(0, parent, null, out enumerator);
            try
            {
                var ids = new IntPtr[128];
                int status;
                do
                {
                    uint count;
                    try
                    {
                        status = enumerator.Next((uint)ids.Length, ids, out count);
                        Marshal.ThrowExceptionForHR(status);
                        for (var i = 0; i < count; i++) result.Add(Marshal.PtrToStringUni(ids[i]));
                    }
                    finally
                    {
                        foreach (var id in ids) Marshal.FreeCoTaskMem(id);
                        Array.Clear(ids, 0, ids.Length);
                    }
                    if (count == 0) break;
                } while (status == 0);
            }
            finally { Release(enumerator); }
            return result;
        }

        public void CopyFile(string id, string path)
        {
            Task.Run(() => CopyFileCore(id, path)).GetAwaiter().GetResult();
        }

        private void CopyFileCore(string id, string path)
        {
            var key = DefaultResource;
            uint optimal = 0;
            IStream stream;
            resources.GetStream(id, ref key, 0, ref optimal, out stream); // STGM_READ
            var readCount = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                var buffer = new byte[optimal == 0 ? 65536 : Math.Min(optimal, 1024 * 1024)];
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    while (true)
                    {
                        stream.Read(buffer, buffer.Length, readCount);
                        var read = Marshal.ReadInt32(readCount);
                        if (read == 0) break;
                        output.Write(buffer, 0, read);
                    }
                }
            }
            finally { Marshal.FreeCoTaskMem(readCount); Release(stream); }
        }

        public void Dispose()
        {
            Task.Run(() => DisposeCore()).GetAwaiter().GetResult();
        }

        private void DisposeCore()
        {
            Release(keys); keys = null;
            Release(storageKeys); storageKeys = null;
            Release(nameKeys); nameKeys = null;
            Release(visibilityKeys); visibilityKeys = null;
            Release(sizeKeys); sizeKeys = null;
            Release(importKeys); importKeys = null;
            Release(resources); resources = null;
            bulk = null; // This interface shares the properties RCW.
            Release(properties); properties = null;
            Release(content); content = null;
            if (opened) { device.Close(); opened = false; }
            Release(device); device = null;
        }

        private static T Create<T>(string clsid)
        {
            return (T)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid), true));
        }

        private static void Release(object value)
        {
            if (value != null) Marshal.ReleaseComObject(value);
        }

        private static Guid ReadGuid(IPortableDeviceValues values, PropertyKey key)
        {
            Guid value;
            return values.GetGuidValue(ref key, out value) >= 0 ? value : Guid.Empty;
        }

        private static string ReadString(IPortableDeviceValues values, PropertyKey key)
        {
            string value;
            return values.GetStringValue(ref key, out value) >= 0 && !String.IsNullOrEmpty(value) ? value : null;
        }

        private static string ReadModified(IPortableDeviceValues values)
        {
            var key = DateModified;
            PropVariant value;
            var status = values.GetValue(ref key, out value);
            try
            {
                if (status < 0) return null;
                if (value.Type == (ushort)VarEnum.VT_DATE && value.Date > 0)
                    return DateTime.FromOADate(value.Date).ToString("o", CultureInfo.InvariantCulture);
                if (value.Type == (ushort)VarEnum.VT_FILETIME && value.FileTime > 0)
                    return DateTime.FromFileTimeUtc(value.FileTime).ToString("o", CultureInfo.InvariantCulture);
                return null;
            }
            catch (ArgumentException) { return null; }
            finally { PropVariant.PropVariantClear(ref value); }
        }
    }
}
