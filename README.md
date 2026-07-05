<!-- markdownlint-disable MD033 MD041 -->

# MFAAvalonia 修改版

本仓库基于原项目 [MaaXYZ/MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia) 修改，仅记录本仓库相对原仓库的主要差异。

## 相比原仓库的修改

- 默认界面主题改为粉色，并在 SukiUI 内置主题中新增 `Pink` 主题色。
- 更新设置中的默认下载源改为 GitHub。
- `interface.json` 的任务项新增 `exclude_from_select_all` 字段：
  - 当点击主界面“全选”时，配置了该字段的任务不会被选中。
- `interface.json` 支持通过 `group` 字段对主界面任务列表分组：
  - 主界面会按组折叠显示任务。
  - 支持读取组名、显示名称、描述、图标等信息。
  - 支持保留组展开/折叠状态。
- 适配新版 SharpCompress API，修复更新依赖后 `ArchiveFactory.Open`、`ReaderFactory.Open`、`TarArchive.Open` 等接口变更导致的构建失败。
- 更新部分 NuGet 依赖以减少已知漏洞告警。
- 新增 [agents.md](./agents.md)，用于说明项目结构和后续 agent 修改指引。

## 构建

本仓库主项目目标框架为 .NET 10。桌面端发布示例：

```powershell
dotnet publish MFAAvalonia.Desktop\MFAAvalonia.Desktop.csproj -c Release -r win-x64
```

生成的 Windows x64 可执行文件位于：

```text
bin\AnyCPU\Release\win-x64\publish\MFAAvalonia.exe
```

## 致谢

感谢原项目及相关开源项目：

- [MaaXYZ/MFAAvalonia](https://github.com/MaaXYZ/MFAAvalonia)
- [MaaFramework](https://github.com/MaaXYZ/MaaFramework)
- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia)
- [SukiUI](https://github.com/kikipoulet/SukiUI)
- [MaaFramework.Binding.CSharp](https://github.com/MaaXYZ/MaaFramework.Binding.CSharp)
- [Mirror Chyan](https://github.com/MirrorChyan/docs)

也感谢所有为原项目、MaaFramework 生态和相关依赖做出贡献的开发者。
