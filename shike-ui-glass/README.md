# Shike.UI.Glass

独立 WPF 视觉类库，目标框架 net10.0-windows10.0.19041.0，命名空间 Shike.UI.Glass。

包含玻璃资源、基础控件模板、悬停层、选中层、Motion 动画和 GlassMenus 下拉框行为。
本项目不引用 Shike 应用、笔记业务或数据存储。应用通过 ProjectReference 使用它。

## 接入

在应用基础样式安装后调用 GlassTheme.Install(app)，通过 GlassTheme.Apply(opacity)
调整材质透明度，使用 Motion.Enabled 控制动画。基础按钮样式可由宿主提供。
GlassMenus.AnimateMenu(combo) 安装通用菜单行为；可选 beforeOpen 回调用于宿主关闭其他面板。
重复安装同一下拉框行为不会重复绑定事件，回调可后续更新。

Glass.* 资源键保持兼容；应用侧 GlassModuleImports.cs 保留现有短类型名调用。
Theme 的业务配色、GraphGlass 的图谱表现仍归应用所有。
InternalsVisibleTo Shike 仅供现有悬停 UI 测试访问内部诊断入口。

## 构建与验证

在仓库根目录执行：

    dotnet build src-v0.10/Shike.csproj -o 拾课-模块版 -m:1
    dotnet build ui-theme-preview/ThemePreview.csproj -m:1

测试入口保留 --self-test、--theme-ui-test、--ui-test --hover-only 和 --ui-test --graph-glass-only。
发布应用时必须一并携带 Shike.UI.Glass.dll，不能仅替换 Shike.exe。
数据文件格式及上一级 data 目录约定保持不变。
