# SubSsh — 可插拔、可任意替换的 SSH 网关（插件架构）

基于 [SubSSH](https://github.com/Lynricsy/SubSSH)（Go + React，GPL-3.0）的 C# 移植，
架构范式遵循论文 [A Programming Paradigm for Spatiotemporal Composability](https://arxiv.org/abs/2608.25512)
（参考实现 https://github.com/ByboyCn/ImBot ）。

**核心定位：一切皆公共组件插件，可插拔、可任意替换。** 宿主 `SubSsh.Host` 永不改动；
DB、OAuth、WebApi、SSH 池、文件管理、MCP、各功能组都是独立 dll，卸载即级联回滚，换一个实现插件重新装上即可。

## 总体
- 目标框架：net10.0（dotnet 10，所有项目统一），可空引用类型启用，implicit usings 启用。
- 命名空间 = 项目名（如 `SubSsh.Core.Composition`），文件级命名空间，PascalCase。
- JSON：System.Text.Json，camelCase，字段名与 Go 原版 json tag 一致（对外 API/MCP 输出必须与原项目一致）。
- Go goroutine/channel → async/Task + System.Threading.Channels。
- Go embed → EmbeddedResource + 程序集资源读取。
- 错误处理：结构化结果或 `SubSshException`（Core 定义），MCP 层转 isError，文案与 Go 版一致。

## 目录结构（src 三层）
- `src/core/` —— 框架：SubSsh.Core（抽象与组合根）、SubSsh.Host（唯一 exe 宿主）
- `src/components/` —— **提供公共服务的组件插件**（各插件实现 Core 接口对外 Provide）：Plugin.Db、Plugin.OAuth、Plugin.WebApi，以及后续 Crypto/SshPool/ExecRunner/Files/Monitor/HostManager/Jobs/Memory/Search/Denylist/ToolGroups/McpCore/McpStdio/McpHttp
- `src/apps/` —— **应用/功能组插件**（只 Require 公共组件，不对外提供底层服务）：后续 Plugin.Tools.* 工具包等
- `web/` —— Vue 3 前端

解决方案文件：根目录 `SubSsh.slnx`。插件编译后自动部署到 `src/core/SubSsh.Host/bin/<配置>/net10.0/plugins/`（Directory.Build.props）。

## 插件清单与服务依赖（Provide → Require）
| 插件 | Provides | Requires |
|---|---|---|
| Plugin.Settings | `ISettings`（原 config.go：DataDir/Listen/PublicUrl/MasterKey/AdminPassword/Embedding*/PollInterval/SearchHelper/McpApps/DisabledTools） | — |
| Plugin.Db | `IOneSshStore`（SQLite + 15 个 migration 原样嵌入执行；公共 DB 组件） | ISettings |
| Plugin.Crypto | `ICryptoBox`（cryptox.go 等价，密文格式兼容） | ISettings |
| Plugin.SshPool | `IConnectionPool`（sshpool.go：连接复用、TOFU 指纹、跳板链；公共 SSH 组件） | IOneSshStore, ICryptoBox |
| Plugin.ExecRunner | `ICommandRunner`（execx.go：超时/截断/session cwd+env 持久化/后台 job） | IConnectionPool |
| Plugin.Files | `IFileManager`（file read/write/edit/list/find/grep/transfer，sha256 乐观锁、原子写；公共文件组件） | IConnectionPool |
| Plugin.Image | `IImageInspector`（imagex，ImageSharp） | IConnectionPool |
| Plugin.Monitor | `IHostMonitor`（monitor.go 采样解析） | IConnectionPool |
| Plugin.HostManager | `IHostManager`（hostmanager.go：CRUD/test/reset_fingerprint/manage_hosts 权限/标签） | IOneSshStore, ICryptoBox, IConnectionPool |
| Plugin.Jobs | `IJobManager`（jobs：start/status/logs/kill/artifact 日志/维护清理） | IOneSshStore, ICommandRunner, IConnectionPool（Go Manager 持有池） |
| Plugin.Memory | `IMemoryEngine`（memoryx：remember/recall/update/forget/list/stats/sleep，混合打分） | IOneSshStore |
| Plugin.Search | `ISearchEngine`（searchx：rg 优先 + 降级 helper 协议） | IConnectionPool |
| Plugin.Denylist | `IDenylist`（denylist.go 工具分组 denylist：实例级 ∪ 令牌级，fail-closed；审计存储可选挂接） | ISettings（IOneSshStore 可选） |
| Plugin.ToolGroups | `IToolGroups`（toolgroups.go 常量，工具名与原版完全一致） | — |
| Plugin.McpCore | `IMcpServer` + `IToolRegistry`（手写 JSON-RPC 2.0 MCP，不用 SDK；token 认证、disabled_tools、command_runs 审计、fanout；公共 MCP 组件） | IOneSshStore, ISettings |
| Plugin.Tools.*（Exec/Files/Hosts/Jobs/Memory/Misc 六个工具包插件，对应 mcpserver 的 exec/hosts/files/jobs/memory/image/monitor/search 等文件） | — | IMcpServer（注册工具用 Track）+ 对应能力服务 |
| Plugin.McpStdio | stdio 传输 | IMcpServer |
| Plugin.McpHttp | streamable-http 传输（自宿 Kestrel /mcp） | IMcpServer, ISettings |
| Plugin.OAuth | `IOAuthService`（oauthserver：PKCE、动态客户端注册、.well-known 元数据；公共 OAuth 组件） | IOneSshStore, ISettings |
| Plugin.WebApi | `IWebApiServer`（REST /api/v1 + SSE /api/events + Vue dist SPA 托管；公共 Web 组件） | IOneSshStore, IOAuthService, ISettings |

**任何组件都可被另一个实现同接口的插件替换**（如把 SQLite 换成 Postgres 插件），依赖方无感知。

## 接口归属（防止跨插件重复定义）
- 所有服务接口定义在 **SubSsh.Core**（`SubSsh.Core.Abstractions` 命名空间）；数据模型（HostConfig/SshKey/ApiToken/AuditEntry/JobRecord/MemoryRecord/CommandRun，字段对齐 store/models.go）在 `SubSsh.Core.Models`。
- 各插件只引用 SubSsh.Core + 实现自己的服务；运行时通过 `Require<T>` 拿到实例，不直接引用其他插件项目。

## 可组合性规则（论文核心，必须遵守）
1. **时间可组合**：插件一切修改通过 `ctx.Track(apply, revert)` 登记；`StartAsync` 只做登记，卸载按 LIFO 自动回滚——插件作者不写清理代码。
2. **空间可组合**：`ctx.Provide<T>(impl)` / `ctx.Require<T>(onAdd, onRemove)`；提供者上线依赖者自动激活、下线自动停用（静默等待不报错）。
3. **级联卸载**：卸载提供者时递归卸载消费方。
4. 管理平面 `IAdminApi` 是宿主级服务，不随任何插件卸载。
5. 发现用 `MetadataLoadContext`（不锁 dll），`:load` 才装入独立 collectible `AssemblyLoadContext`，卸载后 GC 回收、文件锁释放。

## 迁移 SQL 与数据库
store/migrations/*.sql 原样保存为 Plugin.Db 的 EmbeddedResource，启动按序执行；Microsoft.Data.Sqlite，表结构用原版 migration 不改写。包版本：Renci.SshNet 2024.2.0、SixLabors.ImageSharp 3.1.12、Microsoft.Data.Sqlite 10.x。
