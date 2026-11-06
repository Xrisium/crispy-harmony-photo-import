using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace CrispyHarmonyPhotoImport
{
    // Loaded by the import script from this EXE. All device handles live in that process.
    public sealed class MtpDevice : IDisposable
    {
        private const string ObjectProperties = "ef6b490d-5cd8-437a-affc-da8b60ee4a3c";
        private const string ClientProperties = "204d9f0c-2292-4080-9f42-40664e70f859";
        private const string Root = "DEVICE";
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
        private static readonly HashSet<string> AlbumFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "DCIM", "Pictures", "Movies", "Download", "Downloads", "Screenshots" };
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
        private bool opened;

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
            public long Size { get; internal set; }
            public string Modified { get; internal set; }
        }

        public sealed class Album
        {
            public string Id { get; internal set; }
            public string Folder { get; internal set; }
            public string Storage { get; internal set; }
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
                keys = Create<IPortableDeviceKeyCollection>("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
                foreach (var key in new[] { ObjectName, OriginalName, ContentType, IsHidden, ObjectSize, DateModified, FunctionalCategory })
                {
                    var property = key;
                    keys.Add(ref property);
                }
            }
            catch { Dispose(); throw; }
        }

        public Album[] GetAlbums()
        {
            var albums = new List<Album>();
            var storageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in GetChildren(Root))
            {
                IPortableDeviceValues values;
                properties.GetValues(id, keys, out values);
                string storage;
                try
                {
                    if (ReadGuid(values, FunctionalCategory) != StorageType) continue;
                    storage = ReadString(values, ObjectName);
                }
                finally { Release(values); }
                if (String.IsNullOrWhiteSpace(storage)) throw new IOException("手机存储未提供可用的名称。");
                if (!storageNames.Add(storage)) throw new IOException("手机上有重名的存储卷：" + storage);
                foreach (var folder in ReadEntries(id, true))
                    albums.Add(new Album { Id = folder.Id, Folder = folder.Name, Storage = storage });
            }
            if (storageNames.Count == 0) throw new IOException("手机未开放存储。请解锁手机并选择 USB「文件传输」。");
            return albums.ToArray();
        }

        public Entry[] GetEntries(string parent) { return ReadEntries(parent, false).ToArray(); }

        private List<Entry> ReadEntries(string parent, bool albumsOnly)
        {
            var entries = new List<Entry>();
            foreach (var id in GetChildren(parent))
            {
                IPortableDeviceValues values;
                properties.GetValues(id, keys, out values);
                try
                {
                    var hidden = IsHidden;
                    int flag;
                    if (values.GetBoolValue(ref hidden, out flag) >= 0 && flag != 0) continue;
                    var type = ReadGuid(values, ContentType);
                    var folder = type == FolderType;
                    var name = ReadString(values, OriginalName) ?? ReadString(values, ObjectName);
                    if (name != null && name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (albumsOnly && (!folder || !AlbumFolders.Contains(name))) continue;
                    if (!folder && type != ImageType && type != VideoType)
                    {
                        var extension = name == null ? -1 : name.LastIndexOf('.');
                        if (extension < 0 || !MediaExtensions.Contains(name.Substring(extension))) continue;
                    }
                    if (String.IsNullOrEmpty(name)) throw new IOException("手机媒体未提供原始文件名。");
                    var entry = new Entry { Id = id, Name = name, IsFolder = folder };
                    if (!folder)
                    {
                        var sizeKey = ObjectSize;
                        ulong size;
                        if (values.GetUnsignedLargeIntegerValue(ref sizeKey, out size) < 0 || size > long.MaxValue)
                            throw new IOException("手机未提供可核验的文件大小：" + name);
                        entry.Size = (long)size;
                        entry.Modified = ReadModified(values);
                    }
                    entries.Add(entry);
                }
                finally { Release(values); }
            }
            return entries;
        }

        private List<string> GetChildren(string parent)
        {
            var result = new List<string>();
            IEnumPortableDeviceObjectIds enumerator;
            content.EnumObjects(0, parent, null, out enumerator);
            try
            {
                var ids = new IntPtr[32];
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
            Release(keys); keys = null;
            Release(resources); resources = null;
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
