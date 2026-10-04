# Werewolf.Server 项目长期记忆

## 项目结构（4 个项目 + 1 测试宿主）
- `WerewolfGame/`：ASP.NET Core + SignalR 服务端（程序集名 `Werewolf.Server`，net8.0），监听 `http://0.0.0.0:5000`，Hub 路径 `/gamehub`
- `Werewolf.Client.Share/`：共享库（net8.0）——**协议与网络层的唯一来源**，两端禁止各自定义协议
- `ClientGUI/`：WPF 客户端（net8.0-windows），轻量手写 MVVM
- `Werewolf.Client/`：控制台客户端
- `ClientGUI.Tests/`：自带断言的最小测试宿主（纯 exe，无测试框架依赖）

## 服务端架构（v2.0 多房间）
- 两层结构：**注册表 `RoomManager`**（`ConcurrentDictionary` 存 `_rooms`/`_connRoom`/`_connUser`，无后台线程，可直接 new 便于测试）
  + **房间实例 `GameRoom`**（各自一把 `_sync` 锁，自持玩家/阶段/夜晚数据）。**不存在单例房间**。
- 空房回收由 `RoomRecycleService : BackgroundService` 驱动（`EmptyGraceSeconds=60s`、扫描 10s）。
- 房间隔离靠「`EncryptedChannel` 按 connId 单发 + `GameRoom.Broadcast*` 只遍历本房间玩家」，注册表不参与推送。
- `OnDisconnectedAsync` 清理顺序固定：`_sessions.Remove(connId)` → `_rooms.LeaveByConnection(connId)` → 广播。
- 账号用 `AccountStore`（SHA256 + 快速幂混淆，`v2:` 前缀）；房间密码 `RoomPassword.Hash(roomId,pwd)`（带房间号盐，仅内存）。

## 网络协议铁律
- 业务**全部**走单一加密入口 `Call`（信封：`RpcRequest{Op,Payload}` / `RpcReply{Ok,Msg,Payload}`）；
  新增功能 = 新增 `Ops` 常量 + Payload，**不新增 SignalR 方法**（对外只有 `Handshake`/`Confirm`/`Call`）。
- 握手：PSK 认证的 DH（`HandshakeCrypto`），`Call` 前必须 `Confirm` 通过。
- 服务端业务异常一律 `return RpcReply.Error(...)`，**不要抛 `HubException`**（会走 SignalR 错误通道、破坏统一响应形状）。
- 错误文案统一放 `ServerMessages`（共享库），客户端只显示服务端返回的 `Msg`，不自行拼文案。
- `GameRules`（时长/夜晚顺序/人数上下限）来自 `appsettings.json` 的 `GameRules` 节点，随 `GameStateDto.Rules` 下发。
  **坑**：配置绑定器会**追加**到已有的集合属性，集合初值必须留空，缺省值放到 `ResolveNightOrder` 补齐。

## 构建与验证
- 编译：`dotnet build WerewolfGame.sln`，本项目**对警告零容忍**（`Nullable` 开启，必须 0 警告 0 错误）。
- 测试宿主（在 `ClientGUI.Tests/bin/Debug/net8.0-windows` 下用 `dotnet ClientGUI.Tests.dll`）：
  - 默认 → 纯逻辑/加密/规则/多房间断言
  - `--ui` → WPF 界面冒烟
  - `--server <url>` → 端到端联调
  - `--rooms <url>` → 多房间并行与隔离
  - `--lifecycle <url>` → 结束重置 / 空房回收 / 房间上限（需小局临时配置）
  - `--reconnect <url> [秒]` → 断线重连
  - `--hold` / `--bots` → 凑人数 / 机器人自动对局

## 本机环境坑（实测）
- `.exe`（apphost）跑不起来，一律用 `dotnet <dll>`。
- 访问 localhost 前先 `unset HTTP_PROXY HTTPS_PROXY http_proxy https_proxy`。
- 服务端要在**临时目录**里跑（避免污染仓库 `WerewolfGame/accounts.json`）：拷 `appsettings.json` 到临时目录再 `cd` 过去启动。
- 停服务端只杀监听 5000 的 PID（`netstat -ano | grep :5000`），**不要** `taskkill /IM dotnet.exe`。
- 中文控制台输出可能乱码但可读。

## WPF 坑
- 窗口/UserControl **根元素** Background/Foreground 必须 `DynamicResource`（解析时看不到自身 Resources）。
- 每个窗口都要自己用 `pack://application:,,,/ClientGUI;component/Themes/Panels.xaml` 合并主题字典。
- `PasswordBox.Password` 不可绑定，用 `PasswordChanged` 手动同步。
- 不要在 `ItemsControl` 内用 `DataTrigger` 监听自身 `Count`（无限测量递归）。
- 未 `Show()` 的窗口没有呈现源，Popup 不实例化内部项模板。

## 团队协作（software-company SOP）
中大型需求走「产品经理 PRD → 架构师设计+任务分解 → 工程师实现 → QA 独立验证」；
文档落 `docs/<专题>/PRD.md`、`docs/<专题>/ARCHITECTURE.md`；裁定与验收记录追加到架构文档末尾（§11 模式）。
