using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("crispy-harmony-photo-import")]
[assembly: AssemblyProduct("crispy-harmony-photo-import")]
[assembly: AssemblyDescription("从安卓和鸿蒙手机复制照片和视频")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace CrispyHarmonyPhotoImport
{
    internal static class Program
    {
        internal const string ProjectName = "crispy-harmony-photo-import";

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectName);
                Application.Run(new ImportWindow(Payload.Prepare(appData), appData));
            }
            catch (Exception error)
            {
                MessageBox.Show("无法打开照片导入工具。请确认电脑允许当前用户运行此程序。\n\n" + error.Message,
                    ProjectName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal static class Payload
    {
        internal static string Prepare(string appData)
        {
            using (var input = typeof(Payload).Assembly.GetManifestResourceStream(Program.ProjectName + ".payload.zip"))
            using (var sha = SHA256.Create())
            {
                if (input == null) throw new InvalidDataException("程序内置文件缺失，请重新获取完整的 EXE。");
                var version = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").Substring(0, 24);
                var root = Path.GetFullPath(Path.Combine(appData, "payload", version));
                input.Position = 0;
                using (var mutex = new Mutex(false, "Local\\" + Program.ProjectName + "-" + version))
                {
                    try { mutex.WaitOne(); }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        using (var zip = new ZipArchive(input, ZipArchiveMode.Read, true))
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.Name.Length == 0) continue;
                                var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("程序内置文件路径无效。");
                                using (var data = entry.Open())
                                using (var content = new MemoryStream())
                                {
                                    data.CopyTo(content);
                                    var bytes = content.ToArray();
                                    if (File.Exists(path) && Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))) ==
                                        Convert.ToBase64String(sha.ComputeHash(bytes))) continue;
                                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                                    File.WriteAllBytes(path, bytes);
                                }
                            }
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return root;
            }
        }
    }

    internal sealed class ImportWindow : Form
    {
        private readonly string payload;
        private readonly string appData;
        private readonly string hdcOverride;
        private readonly ComboBox devices = Choice("手机");
        private readonly ComboBox albums = Choice("相册");
        private readonly TextBox destination = new TextBox { Dock = DockStyle.Fill, AccessibleName = "照片保存位置" };
        private readonly Button refresh = MakeButton("检测手机", 114);
        private readonly Button browse = MakeButton("选择文件夹", 114);
        private readonly Button start = MakeButton("开始导入", 128);
        private readonly Button pause = MakeButton("暂停", 92);
        private readonly Button open = MakeButton("打开保存位置", 132);
        private readonly Button details = MakeButton("详细信息", 96);
        private readonly Label albumSize = MakeLabel("", false);
        private readonly Label state = MakeLabel("连接手机后，选择要导入的相册。", true);
        private readonly Label hint = MakeLabel("", false);
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, AccessibleName = "导入进度" };
        private readonly StringBuilder log = new StringBuilder();
        private bool busy, importing, updatingDevices, closeWhenDone, gotAlbums, finished, paused, empty;
        private string pausePath, lastError, completedPath, progressText;
        private Task estimateTask = Task.FromResult(0);
        private CancellationTokenSource estimateCancellation;

        internal ImportWindow(string payload, string appData, string hdcOverride = null)
        {
            this.payload = payload;
            this.appData = appData;
            this.hdcOverride = hdcOverride;
            Text = Program.ProjectName;
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(745, 520);
            MinimumSize = new Size(650, 555);
            BackColor = Color.FromArgb(245, 248, 252);
            ForeColor = Color.FromArgb(21, 37, 55);

            var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(22, 16, 22, 12) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(page);
            var title = MakeLabel(Program.ProjectName, true);
            title.Font = new Font(Font.FontFamily, 21F, FontStyle.Bold);
            AddRow(page, title);
            AddRow(page, MakeLabel("把手机里的照片和视频复制到这台电脑。", false));

            AddRow(page, MakeLabel("1  连接手机", true));
            AddRow(page, Pair(devices, refresh));
            AddRow(page, MakeLabel("解锁并连接手机。安卓选择 USB 文件传输，鸿蒙开启 USB 调试。", false));
            var help = new LinkLabel { Text = "手机上怎样设置 USB？", AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
            help.LinkClicked += delegate
            {
                MessageBox.Show(this,
                    "安卓手机接好数据线并解锁，点手机的 USB 通知，选择“文件传输”或“MTP”。如有访问数据的提示，点“允许”。\n\n" +
                    "鸿蒙手机在设置中搜索“软件版本”，连续点击七次，按提示开启开发者选项。打开设置 → 系统 → 开发者选项 → USB 调试，连接后允许这台电脑。\n\n" +
                    "设置好后，回到电脑点“检测手机”。",
                    "手机连接帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            AddRow(page, help);
            AddRow(page, MakeLabel("2  选择相册和保存位置", true));
            AddRow(page, Pair(albums, albumSize));
            AddRow(page, Pair(destination, browse));
            AddRow(page, state);
            AddRow(page, progress);
            hint.MaximumSize = new Size(630, 0);
            AddRow(page, hint);

            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, page.RowCount++);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            actions.Controls.AddRange(new Control[] { start, pause, open, details });
            AddRow(page, actions);
            start.BackColor = Color.FromArgb(18, 90, 180);
            start.ForeColor = Color.White;
            start.FlatStyle = FlatStyle.Flat;
            start.FlatAppearance.BorderSize = 0;

            destination.Text = InitialDestination();
            refresh.Click += async delegate { await ReadAlbumsAsync(); };
            start.Click += async delegate { await StartImportAsync(); };
            pause.Click += delegate { RequestPause(); };
            browse.Click += delegate { ChooseFolder(); };
            open.Click += delegate { OpenFolder(); };
            details.Click += delegate { ShowLog(); };
            devices.SelectedIndexChanged += async delegate
            {
                if (updatingDevices || busy) return;
                albums.Items.Clear();
                await ReadAlbumsAsync();
            };
            albums.SelectedIndexChanged += delegate { UpdateAlbumSize(); RefreshControls(); };
            destination.TextChanged += delegate { RefreshControls(); };
            FormClosing += OnClosing;
            RefreshControls();
            Shown += async delegate { if (hdcOverride == null) await ReadAlbumsAsync(); };
        }

        private static ComboBox Choice(string name)
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = name };
        }

        private static Button MakeButton(string text, int width)
        {
            return new Button { Text = text, Width = width, Height = 34, Margin = new Padding(0, 3, 7, 3), UseVisualStyleBackColor = true };
        }

        private static Label MakeLabel(string text, bool bold)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 5, 0, 3),
                Font = new Font("Microsoft YaHei UI", 10F, bold ? FontStyle.Bold : FontStyle.Regular) };
        }

        private static void AddRow(TableLayoutPanel page, Control control)
        {
            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(control, 0, page.RowCount++);
        }

        private static Control Pair(Control field, Control companion)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            field.Margin = new Padding(0, 6, 8, 4);
            row.Controls.Add(field, 0, 0);
            row.Controls.Add(companion, 1, 0);
            return row;
        }

        private string InitialDestination()
        {
            try
            {
                var previous = Path.Combine(appData, "last-destination.txt");
                if (!File.Exists(previous))
                    previous = Path.Combine(Path.GetDirectoryName(appData), "HarmonyPhotoImport", "last-destination.txt");
                if (File.Exists(previous))
                {
                    var path = File.ReadAllText(previous, Encoding.UTF8).Trim();
                    if (Path.IsPathRooted(path)) return path;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (String.IsNullOrEmpty(pictures)) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            var date = DateTime.Now.ToString("yyyyMMdd");
            var current = Path.Combine(pictures, date + "-" + Program.ProjectName);
            var legacy = Path.Combine(pictures, date + "-huawei-export");
            if (!Directory.Exists(current) && Directory.Exists(Path.Combine(legacy, ".harmony-import"))) return legacy;
            return current;
        }

        private void RefreshControls()
        {
            refresh.Enabled = !busy;
            devices.Enabled = !busy && devices.Items.Count > 0;
            albums.Enabled = !busy && albums.Items.Count > 0;
            destination.Enabled = browse.Enabled = !busy;
            start.Enabled = !busy && albums.SelectedItem != null && !String.IsNullOrWhiteSpace(destination.Text);
            pause.Enabled = busy && importing && pausePath != null;
            open.Enabled = !busy && completedPath != null && Directory.Exists(completedPath);
        }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择照片和视频的保存位置", ShowNewFolderButton = true })
            {
                if (Directory.Exists(destination.Text)) dialog.SelectedPath = destination.Text;
                else
                {
                    try
                    {
                        var parent = Path.GetDirectoryName(destination.Text);
                        if (Directory.Exists(parent)) dialog.SelectedPath = parent;
                    }
                    catch (ArgumentException) { }
                }
                if (dialog.ShowDialog(this) == DialogResult.OK) destination.Text = dialog.SelectedPath;
            }
        }

        private void OpenFolder()
        {
            try { Process.Start(new ProcessStartInfo(completedPath) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法打开保存位置"); }
        }

        private void ShowLog()
        {
            using (var window = new Form { Text = "导入详细信息", Size = new Size(750, 480), StartPosition = FormStartPosition.CenterParent })
            {
                var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = Font };
                lock (log) text.Text = log.ToString();
                window.Controls.Add(text);
                window.ShowDialog(this);
            }
        }

        internal async Task ReadAlbumsAsync()
        {
            if (busy || closeWhenDone) return;
            gotAlbums = false;
            finished = false;
            var oldAlbum = albums.SelectedItem as Album;
            albums.Items.Clear();
            state.Text = "正在检测手机…";
            hint.Text = "";
            await RunAsync("browse", oldAlbum, "");
            if (gotAlbums) UpdateAlbumSize();
            if (!gotAlbums)
            {
                var connected = devices.SelectedItem as Phone;
                if (devices.Items.Count > 1 && connected == null)
                {
                    state.Text = "找到多台设备";
                    hint.Text = "请从下拉框选择要导入的手机。";
                }
                else if (connected != null && connected.State == "Connected")
                {
                    state.Text = "手机已连接，但无法读取相册";
                    hint.Text = connected.Kind == "Mtp" ? "请解锁手机并选择 USB 文件传输，再点“检测手机”。"
                        : "请确认手机已解锁并允许 USB 调试。具体原因见“详细信息”。";
                }
                else
                {
                    state.Text = connected != null && connected.State == "Unauthorized" ? "请在手机上点“允许”" : "还没有连上手机";
                    hint.Text = "安卓请选择 USB 文件传输，鸿蒙请允许 USB 调试，然后再次检测。";
                }
                if (lastError != null && devices.Items.Count == 0 && !gotDevices)
                {
                    state.Text = "无法启动手机检测";
                    hint.Text = "请关闭后重新打开程序。若仍失败，请查看“详细信息”。";
                }
            }
        }

        private bool gotDevices;

        private void UpdateAlbumSize()
        {
            CancelEstimate();
            var album = albums.SelectedItem as Album;
            var phone = devices.SelectedItem as Phone;
            if (album == null || phone == null) { albumSize.Text = ""; return; }
            if (album.Size.HasValue) { ShowAlbumSize(album, album.Size.Value); return; }
            albumSize.Text = "正在估算…";
            if (busy || closeWhenDone) return;
            estimateCancellation = new CancellationTokenSource();
            estimateTask = EstimateAsync(estimateTask, phone, album, estimateCancellation);
        }

        private void CancelEstimate()
        {
            if (estimateCancellation != null) estimateCancellation.Cancel();
        }

        private async Task EstimateAsync(Task previous, Phone phone, Album album, CancellationTokenSource cancellation)
        {
            try
            {
                await previous;
                cancellation.Token.ThrowIfCancellationRequested();
                var bytes = await Task.Run(() => ReadAlbumSize(phone, album, cancellation.Token));
                if (!cancellation.IsCancellationRequested) ShowAlbumSize(album, bytes);
            }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested)
                {
                    albumSize.Text = "暂无法估算大小";
                    AppendLog("相册大小估算失败。" + error.Message);
                }
            }
            finally
            {
                if (estimateCancellation == cancellation) estimateCancellation = null;
                cancellation.Dispose();
            }
        }

        private long ReadAlbumSize(Phone phone, Album album, CancellationToken cancellation)
        {
            string request = CreateRequest("estimate", phone, album, "");
            try
            {
                long? bytes = null;
                string error = null;
                string output = null;
                int code = RunBackend(request, data =>
                {
                    switch ((string)data["kind"])
                    {
                        case "albumSize": bytes = Convert.ToInt64(data["totalBytes"]); break;
                        case "error": error = (string)data["message"]; break;
                    }
                }, line => { if (!String.IsNullOrWhiteSpace(line)) output = line.Trim(); }, cancellation);
                if (code != 0 || !bytes.HasValue) throw new IOException(error ?? output ?? "未收到相册大小。退出代码 " + code);
                return bytes.Value;
            }
            finally { TryDelete(request); }
        }

        private void ShowAlbumSize(Album album, long bytes)
        {
            album.Size = bytes;
            albumSize.Text = "约 " + FormatSize(bytes);
        }

        private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

        private static string FormatSize(long bytes)
        {
            decimal value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < SizeUnits.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString("0.##") + " " + SizeUnits[unit];
        }

        internal async Task StartImportAsync()
        {
            var selected = albums.SelectedItem as Album;
            if (busy || closeWhenDone || selected == null) return;
            string path;
            try
            {
                var text = destination.Text.Trim().Trim('"');
                if (!Path.IsPathRooted(text)) throw new ArgumentException("请选择一个保存文件夹。");
                path = Path.GetFullPath(text);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "请选择保存位置", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Directory.CreateDirectory(appData);
                File.WriteAllText(Path.Combine(appData, "last-destination.txt"), path, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finished = paused = empty = false;
            completedPath = null;
            state.Text = "正在准备导入…";
            hint.Text = "保持手机连接。导入完成后可在这里打开保存位置。";
            await RunAsync("import", selected, path);
            if (!selected.Size.HasValue) albumSize.Text = "大小未估算";
            if (paused)
            {
                ShowProgress("导入已暂停");
                hint.Text = "保持原保存位置，再点“开始导入”即可继续。";
                if (Directory.Exists(path)) completedPath = path;
            }
            else if (empty)
            {
                state.Text = "这个相册没有可复制的文件";
                hint.Text = "可选择其他相册。云端照片请先下载到手机。";
            }
            else if (!finished)
            {
                ShowProgress("导入未完成");
                hint.Text = FriendlyError(lastError);
                if (Directory.Exists(path)) completedPath = path;
            }
            RefreshControls();
        }

        private static string FriendlyError(string error)
        {
            if (error != null && error.Contains("空间不足"))
                return "保存位置空间不足。请腾出空间，或选择其他文件夹。";
            if (error != null && (error.Contains("同名文件") || error.Contains("另一台手机")))
                return "这个位置已有其他文件或另一台手机的导入记录。请选择新文件夹。";
            return "检查手机连接后可用同一保存位置继续。具体原因见“详细信息”。";
        }

        private async Task RunAsync(string action, Album selected, string path)
        {
            busy = true;
            importing = action == "import";
            gotDevices = false;
            lastError = null;
            var phone = devices.SelectedItem as Phone;
            string requestFile = null;
            progressText = null;
            progress.Style = ProgressBarStyle.Marquee;
            RefreshControls();
            try
            {
                CancelEstimate();
                await estimateTask;
                if (closeWhenDone) return;
                lock (log) log.Clear();
                requestFile = CreateRequest(action, phone, selected, path);
                pausePath = importing ? requestFile + ".pause" : null;
                RefreshControls();
                int code = await Task.Run(() => RunBackend(requestFile,
                    data => BeginInvoke((Action)(() => HandleEvent(data, selected))), AppendLog, CancellationToken.None));
                if (code != 0 && code != 2 && lastError == null) lastError = "导入进程退出，代码 " + code;
            }
            catch (Exception error)
            {
                lastError = error.Message;
                AppendLog(error.ToString());
            }
            finally
            {
                TryDelete(pausePath);
                TryDelete(requestFile);
                pausePath = null;
                busy = importing = false;
                if (!finished && progressText == null) { progress.Style = ProgressBarStyle.Blocks; progress.Value = 0; }
                RefreshControls();
                if (closeWhenDone) BeginInvoke((Action)Close);
            }
        }

        private string CreateRequest(string action, Phone phone, Album selected, string path)
        {
            var jobDir = Path.Combine(appData, "jobs");
            Directory.CreateDirectory(jobDir);
            var requestFile = Path.Combine(jobDir, Guid.NewGuid().ToString("N") + ".json");
            var request = new { action = action, device = phone == null ? "" : phone.Id,
                deviceKind = phone == null ? "" : phone.Kind, destination = path,
                album = selected == null ? "相机" : selected.Name, allAlbums = selected != null && selected.All };
            try { File.WriteAllText(requestFile, new JavaScriptSerializer().Serialize(request), new UTF8Encoding(false)); }
            catch { TryDelete(requestFile); throw; }
            return requestFile;
        }

        private int RunBackend(string request, Action<Dictionary<string, object>> onEvent, Action<string> onLog,
            CancellationToken cancellation)
        {
            var arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(Path.Combine(payload, "Import-HarmonyPhotos.ps1"))
                + " -GuiRequest " + Quote(request);
            if (hdcOverride != null) arguments += " -HdcPath " + Quote(hdcOverride);
            else arguments += " -MtpAssembly " + Quote(Application.ExecutablePath);
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = payload
            };
            cancellation.ThrowIfCancellationRequested();
            using (var process = Process.Start(info))
            using (cancellation.Register(() =>
            {
                // Estimate jobs only read the phone, so a change of selection can interrupt them.
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }))
            {
                var error = process.StandardError.ReadToEndAsync();
                var serializer = new JavaScriptSerializer();
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    const string prefix = "HARMONY_GUI ";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        try
                        {
                            var data = serializer.Deserialize<Dictionary<string, object>>(line.Substring(prefix.Length));
                            onEvent(data);
                        }
                        catch (ArgumentException) { onLog(line); }
                    }
                    else onLog(line);
                }
                process.WaitForExit();
                onLog(error.GetAwaiter().GetResult());
                return process.ExitCode;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + Regex.Replace(Regex.Replace(value, "(\\\\*)\"", "$1$1\\\""), "(\\\\+)$", "$1$1") + "\"";
        }

        private void HandleEvent(Dictionary<string, object> data, Album selected)
        {
            switch ((string)data["kind"])
            {
                case "devices":
                    gotDevices = true;
                    if (importing) break;
                    updatingDevices = true;
                    devices.Items.Clear();
                    int chosen = -1;
                    var selection = data["selected"] as Dictionary<string, object>;
                    foreach (Dictionary<string, object> item in (System.Collections.IEnumerable)data["items"])
                    {
                        var phone = new Phone { Id = (string)item["Id"], Name = (string)item["Name"],
                            State = (string)item["State"], Kind = (string)item["Kind"] };
                        if (selection != null && phone.Id == (string)selection["Id"] && phone.Kind == (string)selection["Kind"])
                            chosen = devices.Items.Count;
                        devices.Items.Add(phone);
                    }
                    devices.SelectedIndex = chosen;
                    updatingDevices = false;
                    break;
                case "albums":
                    gotAlbums = true;
                    albums.Items.Clear();
                    int camera = 0, old = -1;
                    foreach (string name in (System.Collections.IEnumerable)data["items"])
                    {
                        if (name == (string)data["defaultAlbum"]) camera = albums.Items.Count;
                        if (selected != null && !selected.All && selected.Name == name) old = albums.Items.Count;
                        albums.Items.Add(new Album { Name = name });
                    }
                    if (albums.Items.Count == 0)
                    {
                        state.Text = "手机已连接，但没有可见相册";
                        hint.Text = "确认照片已保存在手机上，再点击“检测手机”。安卓会查找 DCIM 等常用媒体目录。";
                        break;
                    }
                    albums.Items.Add(new Album { All = true, Name = "全部可见相册" });
                    albums.SelectedIndex = selected != null && selected.All ? albums.Items.Count - 1 : old >= 0 ? old : camera;
                    state.Text = "手机已连接，可以开始导入";
                    hint.Text = "确认相册和保存位置，然后点击“开始导入”。";
                    break;
                case "phase":
                    if (pause.Enabled) state.Text = (string)data["message"];
                    break;
                case "albumSize":
                    ShowAlbumSize(selected, Convert.ToInt64(data["totalBytes"]));
                    break;
                case "progress":
                    var done = Convert.ToInt32(data["done"]);
                    var total = Convert.ToInt32(data["total"]);
                    var doneBytes = Convert.ToInt64(data["doneBytes"]);
                    var totalBytes = Convert.ToInt64(data["totalBytes"]);
                    progressText = String.Format("{0} / {1} · {2} / {3} 个文件", FormatSize(doneBytes), FormatSize(totalBytes), done, total);
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = totalBytes > 0 ? (int)(100m * doneBytes / totalBytes) : 0;
                    ShowProgress(pause.Enabled ? "正在导入" : "正在暂停…");
                    break;
                case "complete":
                    finished = true;
                    completedPath = (string)data["destination"];
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = 100;
                    ShowProgress("导入完成");
                    hint.Text = String.Format("新复制 {0} 个文件，已核验 {1} 个已有文件。", data["copied"], data["skipped"]);
                    break;
                case "empty": empty = true; break;
                case "paused": paused = true; break;
                case "error": lastError = (string)data["message"]; AppendLog(lastError); break;
            }
        }

        private void ShowProgress(string status)
        {
            state.Text = progressText == null ? status : status + " · " + progressText;
        }

        private void RequestPause()
        {
            if (pausePath == null) return;
            try
            {
                File.WriteAllText(pausePath, "pause");
                pause.Enabled = false;
                ShowProgress("正在暂停…");
                hint.Text = "当前文件处理完后就会暂停。";
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法请求暂停"); }
        }

        private async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy)
            {
                if (estimateTask.IsCompleted) return;
                e.Cancel = true;
                if (closeWhenDone) return;
                closeWhenDone = true;
                CancelEstimate();
                await estimateTask;
                Close();
                return;
            }
            e.Cancel = true;
            if (!importing || MessageBox.Show(this, "当前文件处理完后暂停并关闭窗口？", "关闭导入工具", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                closeWhenDone = true;
                if (importing) RequestPause();
            }
        }

        private void AppendLog(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return;
            lock (log)
            {
                log.AppendLine(text);
                if (log.Length > 80000) log.Remove(0, log.Length - 60000);
            }
        }

        private static void TryDelete(string path)
        {
            if (path == null) return;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private sealed class Phone
        {
            internal string Id, Name, State, Kind;
            public override string ToString()
            {
                if (Kind == "Mtp") return "USB 文件传输 · " + Name;
                return "鸿蒙 · " + Name + (State == "Connected" ? "（已连接）" : State == "Unauthorized" ? "（等待手机授权）" : "（未就绪）");
            }
        }

        private sealed class Album
        {
            internal string Name;
            internal bool All;
            internal long? Size;
            public override string ToString() { return Name; }
        }
    }
}
