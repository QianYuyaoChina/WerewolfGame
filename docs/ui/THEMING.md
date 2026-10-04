# 界面主题（深色 / 浅色 / 跟随系统）

> 适用范围：`ClientGUI`（WPF 客户端）。服务端与控制台不涉及。

## 1. 文件结构

| 文件 | 职责 |
|------|------|
| `Themes/Colors.Dark.xaml` | **深色配色的唯一定义处** |
| `Themes/Colors.Light.xaml` | **浅色配色的唯一定义处**（键名必须与深色完全一致） |
| `Themes/Converters.xaml` | 与主题无关的转换器（`BoolToVisibility` 等） |
| `Themes/Controls.xaml` | 控件样式（按钮 / 输入框 / 菜单…）——**不含任何颜色字面量** |
| `Themes/Panels.xaml` | 玩家行 / 操作按钮 / 消息模板；合并 Converters + Controls |
| `Services/ThemeManager.cs` | 挂载与切换颜色字典、跟随系统、持久化偏好 |

窗口只合并 `Panels.xaml`（其内部已合并 Converters + Controls）；
**颜色不在窗口里合并**，由 `ThemeManager` 挂到 `Application` 级。

## 2. 铁律

1. **颜色一律用 `{DynamicResource XxxBrush}`**，禁止写死 `#RRGGBB`。
2. **不要用 `{StaticResource XxxBrush}` 引颜色**：StaticResource 在加载时求值一次，
   换主题不会更新（只有样式键如 `FlatButton` 才该用 StaticResource）。
3. 新增颜色时 **两套配色都要加同名键**。
   `dotnet ClientGUI.Tests.dll --ui` 会自动扫描所有 XAML 里引用的 `*Brush` 键，
   缺任何一个都会断言失败（漏定义的后果是静默回退成默认色，深色底上等于「看不见的文字」）。
4. 窗口 / UserControl 的**根元素** Background、Foreground 必须用 DynamicResource
   （解析这些属性时还看不到本元素下方声明的 Resources）。

## 3. ThemeManager

```csharp
ThemeManager.Initialize();               // App.OnStartup 里调用一次（早于任何窗口）
ThemeManager.SetTheme(AppTheme.Light);   // 切换并持久化（界面菜单调用）
ThemeManager.Preference                  // System / Dark / Light
ThemeManager.IsDark                      // 当前实际生效
```

- **切换机制**：把 `Colors.Dark.xaml` 换成 `Colors.Light.xaml`（同一个 Application 资源槽位），
  DynamicResource 会重新求值 → **已打开的窗口立刻换色**，无需重建样式、无需遍历窗口。
- **跟随系统**：读 `HKCU\...\Themes\Personalize\AppsUseLightTheme`（1=浅色），
  并用 `DispatcherTimer` 每 4 秒轮询一次（避免引入 `SystemEvents` 依赖）。
- **持久化**：写在 `%APPDATA%\WerewolfClient\ui-state.json` 的 `Theme` 字段。

## 4. 界面入口

**设置窗口（`Views/SettingsWindow`）是唯一的设置入口**，主题在其「外观」分组里：

- 全局快捷键 **`Ctrl + ,`** —— 登录界面、房间列表、主窗口以及各对话框都可用
  （由 `App` 通过 `EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, …)` 统一接管，
  用 Preview 是为了不被输入框吞掉）。
- 主窗口菜单「视图 → 设置…」；登录窗口与房间列表窗口上的「设置」按钮。

设置窗口还会管理主界面三栏显隐；改动通过 `SettingsWindow.ViewOptionsChanged` 通知主窗口同步菜单与布局。

## 4.1 菜单外观的坑（曾经出过 bug）

`Menu.ItemContainerStyle` **只作用于菜单栏的第一层**，不会传播到下拉项、更不会传播到嵌套子菜单。
那些项会落回 WPF 默认的浅色系统外观 —— 在深色主题下表现为「背景是白的」。
因此 `Controls.xaml` 末尾额外注册了 **隐式样式**：

```xml
<Style TargetType="MenuItem" BasedOn="{StaticResource FlatMenuItem}" />
<Style TargetType="Menu" BasedOn="{StaticResource FlatMenu}" />
<Style TargetType="Separator" BasedOn="{StaticResource MenuSeparator}" />
```

`--ui` 冒烟会遍历菜单树，断言**每一个 MenuItem（含各层嵌套）的 Template 都是四套主题模板之一**，守住这条。

## 5. 已知限制

- 菜单 Popup 的投影 `DropShadowEffect` 颜色仍是黑色字面量（在浅色主题下投影很轻，观感可接受）。
- Win32 标题栏（非客户区）由系统绘制，不跟随应用主题——这属于 Windows 的既有行为。
- 主题只影响客户端；服务端不感知。
