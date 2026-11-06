# crispy-harmony-photo-import

通过 USB 将安卓和 HarmonyOS 手机里的照片和视频复制到 Windows 电脑。同一个窗口自动检测两种连接方式，支持按相册导入、中断后继续和本地完整性核验。

## 双击 EXE 使用

下载 `crispy-harmony-photo-import-win-x64.exe`，或在本地构建后的 `dist` 目录中找到它。在 Windows 10/11 x64 上双击即可打开中文窗口，无需安装、解压或配置 PATH。同名的 `.exe.sha256` 文件供下载后核验，运行时只需要 EXE。

1. 用支持数据传输的数据线连接并解锁手机。**安卓**在 USB 通知里选择「文件传输」或「MTP」，无需开启 USB 调试。**鸿蒙**按窗口中的帮助开启 USB 调试，并允许这台电脑调试。设置好后点击「检测手机」。有多台就绪设备时，从下拉框选择目标手机。
2. 默认优先选择安卓的 `DCIM` 或鸿蒙的「相机」，也可选择其他相册或「全部可见相册」。相册右侧会显示所选范围的估算大小。保留默认位置或点击「选择文件夹」，然后点击「开始导入」。新导入默认保存到当前用户「图片」目录下的 `yyyyMMdd-crispy-harmony-photo-import`。
3. 导入进度先显示已完成大小和总大小，再显示已完成文件数和总文件数。进度条按字节比例在每个文件处理完成后更新，续传时计入核验通过的已有文件。显示「导入完成」后，点击「打开保存位置」。程序保留原文件名和文件内容。安卓按存储卷和媒体目录保存，鸿蒙按相册保存。

安卓使用 Windows 自带的 USB 文件传输接口，递归读取手机开放的 `DCIM`、`Pictures`、`Movies`、`Download`、`Downloads` 和 `Screenshots` 中的照片和视频。鸿蒙面向提供本地相册接口的 HarmonyOS 6。程序只读取手机上的可见文件。需要时可先在图库下载云端原件。文件内的 EXIF 等信息随原文件保留，电脑端的文件系统修改时间可能不同。

EXE 内含 HDC 和导入脚本，首次启动时会将它们展开到当前用户的本地应用数据目录。EXE 当前未签名，部分电脑可能提示或限制运行。详细操作、连接问题和实现原理见 [说明书](MANUAL.zh-CN.md)。

## 暂停、续传与核验

点击「暂停」后，程序会处理完当前文件再停下。再次点击「开始导入」时选择同一保存位置即可继续。已完成文件须同时满足：手机列表中的大小和修改时间未变化，电脑文件大小正确，且电脑 SHA-256 与上次记录一致。校验不通过的已记录文件会重传；无导入记录的同名文件不会直接覆盖。

续传以单个文件为单位。请保留保存目录中的 `.harmony-import`，并为不同手机使用不同目录。如果安卓手机未提供某个文件的修改时间，续传时会重新复制该文件。传输后会比对手机端列表记录的大小，SHA-256 用来检查电脑端的文件有没有变化。

## 开发和打包

```powershell
# 开发用离线集成测试，需要开发机已有 Python
python -m unittest discover -s tests -v

# 使用系统 .NET Framework 编译器构建 Windows x64 图形程序
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Exe.ps1
```

唯一构建目标是 Windows 10/11 x64 的便携式图形程序。构建后，`dist` 中有 `crispy-harmony-photo-import-win-x64.exe` 和供分发核验的 `.exe.sha256` 文件。Windows 构建工作流会运行离线测试并上传这两个文件。创建 GitHub Release 时可将这两个文件作为附件。

## 许可证

本项目的代码和文档采用 [MIT 许可证](LICENSE)。内置的 HDC 3.2.0b、libusb 及其依赖分别适用上游许可，EXE 内含上游声明和 libusb 源码。[第三方许可与源码](tools/hdc/README.md) · [来源和校验值](tools/hdc/provenance.json)
