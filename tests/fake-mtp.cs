using System;
using System.IO;

namespace CrispyHarmonyPhotoImport
{
    public sealed class MtpDevice : IDisposable
    {
        public const string Id = "TEST-USB";

        public sealed class Phone { public string Id, Name; }
        public sealed class Album { public string Id, Folder, Storage; }
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

        public Album[] GetAlbums()
        {
            return new[] {
                new Album { Id = Prefix + "pictures", Folder = "Pictures", Storage = "SD 卡" },
                new Album { Id = Prefix + "dcim", Folder = "DCIM", Storage = "内部存储" }
            };
        }

        public Entry[] GetEntries(string parent)
        {
            if (parent == Prefix + "dcim") return new[] { new Entry { Id = Prefix + "camera", Name = "Camera", IsFolder = true } };
            if (parent == Prefix + "camera") return new[] {
                new Entry { Id = Prefix + "photo", Name = "照片 O'Brien.jpg", Size = 4, Modified = "2026-01-01T01:02:03" },
                new Entry { Id = Prefix + "video", Name = "视频.mp4", Size = 4, Modified = Mode == "undated" ? null : "2026-01-01T01:02:03" }
            };
            if (parent == Prefix + "pictures") return new[] {
                new Entry { Id = Prefix + "screen", Name = "截图.png", Size = 4, Modified = "2026-01-01T01:02:03" }
            };
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
                default: throw new IOException("Wrong file: " + file);
            }
        }

        public void Dispose() { }
    }
}
