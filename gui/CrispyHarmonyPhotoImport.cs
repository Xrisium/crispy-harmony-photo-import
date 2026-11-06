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
[assembly: AssemblyDescription("从鸿蒙手机复制照片和视频")]
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
        private readonly Label state = MakeLabel("连接手机后，选择要导入的相册。", true);
        private readonly Label hint = MakeLabel("", false);
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, AccessibleName = "导入进度" };
        private readonly StringBuilder log = new StringBuilder();
        private bool busy, importing, updatingDevices, closeWhenDone, gotAlbums, finished, paused, empty;
        private string pausePath, lastError, completedPath;

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
            AddRow(page, MakeLabel("解锁手机并连接数据线，在手机上打开 USB 调试并允许这台电脑。", false));
            var help = new LinkLabel { Text = "手机上怎样打开 USB 调试？", AutoSize = true, Margin = new Padding(0, 2, 0, 8) };
            help.LinkClicked += delegate
            {
                MessageBox.Show(this,
                    "1. 在手机设置里搜索“软件版本”，连续点击七次。\n\n" +
                    "2. 按手机提示开启开发者选项并重启。\n\n" +
                    "3. 打开设置 → 系统 → 开发者选项 → USB 调试。\n\n" +
                    "4. 接好数据线，解锁手机，在提示中点“允许”，再回到电脑点“检测手机”。",
                    "手机连接帮助", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            AddRow(page, help);
            AddRow(page, MakeLabel("2  选择相册和保存位置", true));
            AddRow(page, albums);
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
            devices.SelectedIndexChanged += delegate
            {
                if (updatingDevices || busy) return;
                albums.Items.Clear();
                state.Text = "已选择手机，点击“检测手机”读取相册。";
                RefreshControls();
            };
            albums.SelectedIndexChanged += delegate { RefreshControls(); };
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

        private static Control Pair(Control field, Button button)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            field.Margin = new Padding(0, 6, 8, 4);
            row.Controls.Add(field, 0, 0);
            row.Controls.Add(button, 1, 0);
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
            if (busy) return;
            gotAlbums = false;
            finished = false;
            var oldAlbum = albums.SelectedItem as Album;
            albums.Items.Clear();
            state.Text = "正在检测手机…";
            hint.Text = "";
            await RunAsync("browse", oldAlbum, "");
            if (!gotAlbums)
            {
                var connected = devices.SelectedItem as Phone;
                if (devices.Items.Count > 1 && connected != null && String.IsNullOrEmpty(requestedDevice))
                {
                    state.Text = "找到多台设备";
                    hint.Text = "请选择要导入的手机，再点击“检测手机”。";
                }
                else if (connected != null && connected.State == "Connected")
                {
                    state.Text = "手机已连接，但无法读取相册";
                    hint.Text = "请确认已解锁手机并允许调试，再试一次。具体原因可在“详细信息”中查看。";
                }
                else
                {
                    state.Text = connected != null && connected.State == "Unauthorized" ? "请在手机上点“允许”" : "还没有连上手机";
                    hint.Text = "解锁手机、检查数据线和 USB 调试，再点击“检测手机”。";
                }
                if (lastError != null && devices.Items.Count == 0 && !gotDevices)
                {
                    state.Text = "无法启动手机检测";
                    hint.Text = "请关闭后重新打开程序。若仍失败，请查看“详细信息”。";
                }
            }
        }

        private string requestedDevice;
        private bool gotDevices;

        internal async Task StartImportAsync()
        {
            if (busy || albums.SelectedItem == null) return;
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
            await RunAsync("import", (Album)albums.SelectedItem, path);
            if (paused)
            {
                state.Text = "导入已暂停";
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
                state.Text = "导入未完成";
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
            requestedDevice = phone == null ? "" : phone.Id;
            string requestFile = null;
            progress.Style = ProgressBarStyle.Marquee;
            lock (log) log.Clear();
            RefreshControls();
            try
            {
                var jobDir = Path.Combine(appData, "jobs");
                Directory.CreateDirectory(jobDir);
                requestFile = Path.Combine(jobDir, Guid.NewGuid().ToString("N") + ".json");
                pausePath = importing ? requestFile + ".pause" : null;
                var request = new { action = action, device = requestedDevice, destination = path,
                    album = selected == null ? "相机" : selected.Name, allAlbums = selected != null && selected.All };
                File.WriteAllText(requestFile, new JavaScriptSerializer().Serialize(request), new UTF8Encoding(false));
                RefreshControls();
                int code = await Task.Run(() => RunBackend(requestFile, selected));
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
                if (!finished) { progress.Style = ProgressBarStyle.Blocks; progress.Value = 0; }
                RefreshControls();
                if (closeWhenDone) BeginInvoke((Action)Close);
            }
        }

        private int RunBackend(string request, Album selected)
        {
            var arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(Path.Combine(payload, "Import-HarmonyPhotos.ps1"))
                + " -GuiRequest " + Quote(request);
            if (hdcOverride != null) arguments += " -HdcPath " + Quote(hdcOverride);
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = payload
            };
            using (var process = Process.Start(info))
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
                            BeginInvoke((Action)(() => HandleEvent(data, selected)));
                        }
                        catch (ArgumentException) { AppendLog(line); }
                    }
                    else AppendLog(line);
                }
                process.WaitForExit();
                AppendLog(error.GetAwaiter().GetResult());
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
                    int previous = -1;
                    foreach (Dictionary<string, object> item in (System.Collections.IEnumerable)data["items"])
                    {
                        var phone = new Phone { Id = (string)item["Id"], State = (string)item["State"] };
                        if (phone.Id == requestedDevice) previous = devices.Items.Count;
                        devices.Items.Add(phone);
                    }
                    if (devices.Items.Count > 0) devices.SelectedIndex = previous >= 0 ? previous : 0;
                    updatingDevices = false;
                    break;
                case "albums":
                    gotAlbums = true;
                    albums.Items.Clear();
                    int camera = 0, old = -1;
                    foreach (string name in (System.Collections.IEnumerable)data["items"])
                    {
                        if (name == "相机") camera = albums.Items.Count;
                        if (selected != null && !selected.All && selected.Name == name) old = albums.Items.Count;
                        albums.Items.Add(new Album { Name = name });
                    }
                    if (albums.Items.Count == 0)
                    {
                        state.Text = "手机已连接，但没有可见相册";
                        hint.Text = "确认照片已保存在手机上，再点击“检测手机”。";
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
                case "progress":
                    var done = Convert.ToInt32(data["done"]);
                    var total = Convert.ToInt32(data["total"]);
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = total > 0 ? (int)(100.0 * done / total) : 0;
                    if (pause.Enabled) state.Text = String.Format("正在导入 · 已完成 {0} / {1}", done, total);
                    break;
                case "complete":
                    finished = true;
                    completedPath = (string)data["destination"];
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = 100;
                    state.Text = "导入完成";
                    hint.Text = String.Format("新复制 {0} 个文件，已核验 {1} 个已有文件。", data["copied"], data["skipped"]);
                    break;
                case "empty": empty = true; break;
                case "paused": paused = true; break;
                case "error": lastError = (string)data["message"]; AppendLog(lastError); break;
            }
        }

        private void RequestPause()
        {
            if (pausePath == null) return;
            try
            {
                File.WriteAllText(pausePath, "pause");
                pause.Enabled = false;
                state.Text = "正在暂停…";
                hint.Text = "当前文件处理完后就会暂停。";
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "无法请求暂停"); }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy) return;
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
            internal string Id, State;
            public override string ToString()
            {
                return Id + (State == "Connected" ? "（已连接）" : State == "Unauthorized" ? "（等待手机授权）" : "（未就绪）");
            }
        }

        private sealed class Album
        {
            internal string Name;
            internal bool All;
            public override string ToString() { return Name; }
        }
    }
}
