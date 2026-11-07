using System;
using System.IO;

namespace CrispyHarmonyPhotoImport
{
    public sealed class MtpDevice : IDisposable
    {
        public const string Id = "TEST-USB";
        public bool IncludeModified { get; set; }
        public bool RespectNoMedia { get; set; }

        public sealed class Phone { public string Id, Name; }
        public sealed class Storage { public string Id, Name; }
        public sealed class Entry { public string Id, Name, Modified; public bool IsFolder; public long Size; }

        private static string Mode { get { return Environment.GetEnvironmentVariable("MTP_TEST_MODE"); } }
        private static string Prefix { get { return Environment.GetEnvironmentVariable("MTP_TEST_SESSION") + ":"; } }

        public static Phone[] ListPhones()
        {
            return Mode == "absent" ? new Phone[0] : new[] { new Phone { Id = Id, Name = "安卓测试机" } };
        }

        public MtpDevice(string id)
        {
            if (id != Id) throw new IOException("Wrong device");
        }

        public Storage[] GetStorages()
        {
            return new[] {
                new Storage { Id = Prefix + "sd", Name = "SD 卡" },
                new Storage { Id = Prefix + "internal", Name = "内部存储" }
            };
        }

        private static Entry Folder(string id, string name) { return new Entry { Id = Prefix + id, Name = name, IsFolder = true }; }

        private static Entry Media(string id, string name)
        {
            return new Entry { Id = Prefix + id, Name = name, Size = 4,
                Modified = id == "video" && Mode == "undated" ? null : "2026-01-01T01:02:03" };
        }

        public Entry[] GetEntries(string parent, bool includeFiles = true)
        {
            if (Mode == "roots-only" && parent != Prefix + "sd" && parent != Prefix + "internal")
                throw new IOException("Default browsing descended into a media directory");
            if (parent == Prefix + "nomedia" && RespectNoMedia) return new Entry[0];
            var entries = ReadEntries(parent);
            return includeFiles ? entries : Array.FindAll(entries, entry => entry.IsFolder);
        }

        private Entry[] ReadEntries(string parent)
        {
            if (!parent.StartsWith(Prefix, StringComparison.Ordinal)) throw new IOException("Stale folder ID: " + parent);
            switch (parent.Substring(Prefix.Length))
            {
                case "sd": return new[] { Folder("sd-pictures", "Pictures") };
                case "sd-pictures": return new[] { Media("screen", "截图.png") };
                case "internal": return Mode == "nested"
                    ? new[] { Folder("dcim", "DCIM"), Folder("pictures", "Pictures"), Folder("android", "Android"),
                        Folder("download", "Download"), Folder("movies", "Movies"), Folder("nomedia", "CacheAlbum"), Media("root-photo", "根目录.jpg") }
                    : new[] { Folder("dcim", "DCIM") };
                case "dcim": return Mode == "nested"
                    ? new[] { Folder("camera", "Camera"), Folder("dcim-screens", "Screenshots"), Media("cover", "cover.jpg") }
                    : new[] { Folder("camera", "Camera") };
                case "camera": return Mode == "nested"
                    ? new[] { Media("photo", "照片 O'Brien.jpg"), Media("photo", "照片 O'Brien.jpg"), Media("video", "视频.mp4") }
                    : new[] { Media("photo", "照片 O'Brien.jpg"), Media("video", "视频.mp4") };
                case "dcim-screens": return new[] { Media("dcim-shot", "screen.png") };
                case "pictures": return new[] { Folder("picture-screens", "Screenshots") };
                case "picture-screens": return new[] { Media("picture-shot", "screen.png") };
                case "android": return new[] { Folder("media", "media"), Folder("data", "data"), Folder("obb", "obb") };
                case "data": return new[] { Media("cache-image", "cache.jpg") };
                case "obb": return new[] { Media("obb-image", "cache.jpg") };
                case "nomedia": return new[] { Media("nomedia-image", "cache.jpg") };
                case "media": return new[] { Folder("wechat-package", "com.tencent.mm") };
                case "wechat-package": return new[] { Folder("wechat", "WeiXin") };
                case "wechat": return new[] { Media("wechat-image", "微信.jpg") };
                case "download": return new[] { Media("download-image", "下载.jpg") };
                case "movies": return new Entry[0];
            }
            throw new IOException("Wrong folder: " + parent);
        }

        public void CopyFile(string id, string path)
        {
            if (!id.StartsWith(Prefix, StringComparison.Ordinal)) throw new IOException("Stale object ID: " + id);
            var file = id.Substring(Prefix.Length);
            if (file == "video" && Mode == "disconnect") throw new IOException("Device disconnected");
            switch (file)
            {
                case "photo": File.WriteAllText(path, "jpeg"); break;
                case "video": File.WriteAllText(path, Mode == "short" ? "bad" : "mp4!"); break;
                case "screen": File.WriteAllText(path, "png!"); break;
                case "root-photo": case "cover": case "dcim-shot": case "picture-shot": case "wechat-image": case "download-image":
                case "cache-image": case "obb-image": case "nomedia-image":
                    File.WriteAllText(path, "img!"); break;
                default: throw new IOException("Wrong file: " + file);
            }
        }

        public void Dispose() { }
    }
}
