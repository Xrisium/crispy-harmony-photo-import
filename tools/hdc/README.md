# 随包 HDC 与 libusb

`hdc.exe`（3.2.0b）及 `libusb_shared.dll`（libusb 1.0.28）来自 OpenHarmony 6.0 Release Windows x64 SDK。原始归档及随包文件的 SHA-256 见 [provenance.json](provenance.json)。

- HDC 源码采用 Apache-2.0。随 SDK 分发的 [NOTICE.txt](NOTICE.txt) 包含工具和依赖的上游许可及声明。[HDC 发布标签源码](https://github.com/openharmony/developtools_hdc/tree/OpenHarmony-v6.0-Release)
- OpenHarmony 的 libusb 组件采用 LGPL-2.1-only。[libusb-1.0.28-openharmony-source.zip](libusb-1.0.28-openharmony-source.zip) 保存 OpenHarmony 6.0 Release 标签对应的组件源码，含原始源码归档、补丁、构建配置和 `COPYING`。[源码快照](https://codeload.github.com/openharmony/third_party_libusb/zip/371dea6f11f6642bf5847a41a2d334c511835568)

维护和测试时如需使用自行构建的兼容 libusb，可将 `hdc.exe` 复制到另一个目录，与替换后的 `libusb_shared.dll` 放在一起，再通过导入脚本的内部参数 `-HdcPath` 指定该目录中的 `hdc.exe`。
