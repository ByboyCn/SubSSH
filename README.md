# SubSSH

**可插拔、可任意替换的 SSH 网关** —— 面向 AI Agent 的集中式 SSH 运维网关，提供 MCP 接入、凭据托管、细粒度授权与全程审计。

C# (.NET 10) 实现的 [OneSSH](https://github.com/Lynricsy/OneSSH)（Go，GPL-3.0）移植版，架构范式遵循论文
[A Programming Paradigm for Spatiotemporal Composability](https://arxiv.org/abs/2608.25512)（参考实现 [ImBot](https://github.com/ByboyCn/ImBot)）。

> **核心设计：宿主永远不变，一切皆公共组件插件。**
> DB、OAuth、WebApi、SSH 连接池、文件管理、MCP、各功能组都是 `plugins\` 目录里的独立 dll，
> 提供/消费声明式服务（`Provide<T>` / `Require<T>`），卸载按 LIFO 完整回滚（可撤销效果），
> 换一个同接口插件重新装载即可替换任意实现，依赖方无感知。

## 特性

- **时空可组合插件架构**（论文核心落地）
  - **时间可组合**：插件的一切修改都是可撤销效果（`ctx.Track(apply, revert)`），卸载按 LIFO 自动回滚，插件作者不写清理代码
  - **空间可组合**：`ctx.Provide<T>(impl)` 发布服务、`ctx.Require<T>(onAdd, onRemove)` 声明依赖；提供者上线依赖者自动激活、下线自动停用（静默等待）
  - **级联卸载**：卸载提供者时递归卸载所有消费方
  - **发现与加载分离**：`MetadataLoadContext` 无锁扫描目录（不锁 dll），装载才进独立 collectible `AssemblyLoadContext`，卸载后 GC 回收、文件锁释放
  - **管理平面分离**：`IAdminApi` 是宿主级服务，卸掉任何插件仍可用 `:` 控制台命令装回
- **MCP 接入**：手写 JSON-RPC 2.0（无 SDK），**stdio 与 streamable-http 双传输**，31 个工具与原版逐字段一致
- **凭据托管**：AES-256-GCM 加密入库，密文格式与 Go 原版互解；SSH 主机 TOFU 指纹固定、跳板链（≤5 级）
- **细粒度授权**：令牌级主机白名单、manage_hosts 独立权限、9 分组 43 工具的禁用组（fail-closed）
- **全程审计**：每次工具调用落 audit 表 + command_runs 命令记录（64KiB 首尾预览、artifact 7 天），SSE 实时推送
- **配套 Web UI**：Vue 3 + Tailwind 管理台（仪表盘/主机/密钥/令牌/审计/文件/任务/记忆/监控/终端/OAuth 授权）
- **OAuth 2.0**：PKCE S256、动态客户端注册、RFC 8414/9728 元数据、刷新令牌轮换 + 重放撤销整个 grant

## 快速开始

### 构建

```bash
dotnet build SubSsh.slnx          # 插件自动部署到 Host 的 bin/plugins/
cd web && npm install && npm run build   # 前端（产物自动进 WebApi 插件 dist/）
```

### 运行（常驻服务）

```bash
dotnet run --project src/core/SubSsh.Host
# 宿主控制台命令（: 前缀）
:list        # 查看目录与已加载
:load *      # 全量加载（首次运行执行）
:services    # 查看服务依赖图
:unload db   # 卸载（级联卸载依赖方）
:delete cmd-xxx   # 卸载 + 删 dll
```

默认监听 `127.0.0.1:8080`，打开 `http://127.0.0.1:8080` 进入 Web 控制台（`ONESSH_ADMIN_PASSWORD` 设置管理密码）。

### 运行（MCP stdio）

```bash
dotnet run --project src/core/SubSsh.Host -- --stdio
```

在 Claude/Cursor 等 MCP 客户端中配置：

```json
{ "command": "dotnet", "args": ["run", "--project", "src/core/SubSsh.Host", "--", "--stdio"] }
```

## 目录结构

```
SubSsh.slnx
├── src/core/            框架：SubSsh.Core（组合根+能力接口+模型）、SubSsh.Host（唯一 exe 宿主）
├── src/components/      公共组件插件（各 Provide 一个核心服务，可被同接口插件替换）
│   ├── Plugin.Db          SQLite 存储 + 原版 15 个 migration
│   ├── Plugin.Crypto      AES-256-GCM 密码箱
│   ├── Plugin.SshPool     连接复用池 + TOFU 指纹 + 跳板链
│   ├── Plugin.ExecRunner  远程执行（超时/截断/artifact/session 持久化）
│   ├── Plugin.Files       SFTP（原子写/sha256 乐观锁/流式传输）
│   ├── Plugin.Jobs        后台任务（setsid/TERM→KILL/维护清理）
│   ├── Plugin.Memory      记忆引擎（FTS5 bm25 + 重要度/时近度混合打分）
│   ├── Plugin.Monitor     /proc 采样轮询
│   ├── Plugin.Search      rg/fd 优先 + SFTP 遍历降级
│   ├── Plugin.OAuth       PKCE + 动态客户端注册
│   ├── Plugin.McpCore     JSON-RPC 2.0 + 令牌认证 + fanout + 审计
│   ├── Plugin.WebApi      REST /api/v1 + SSE + WS 终端 + SPA 托管
│   └── Plugin.McpStdio / McpHttp   双传输
├── src/apps/            应用/功能组插件：HostManager、Denylist、Tools.*（31 个 MCP 工具）
└── web/                 Vue 3 + Vite + Tailwind 管理台
```

## MCP 工具（31 个）

| 分组 | 工具 |
|---|---|
| 主机 | hosts_list · hosts_manage_list · host_create · host_update · host_delete · host_test · host_reset_fingerprint |
| 执行 | exec · exec_many · session_env · output_read |
| 文件 | file_read · file_write · file_edit · file_list · file_transfer |
| 搜索 | grep · find |
| 任务 | job_start · job_list · job_status · job_logs · job_kill |
| 记忆 | memory_remember · memory_recall · memory_list · memory_update · memory_forget · memory_stats · memory_sleep |
| 监控 | host_status |

## 环境变量

| 变量 | 说明 | 默认 |
|---|---|---|
| `ONESSH_LISTEN` | Web 监听地址 | `127.0.0.1:8080` |
| `ONESSH_ADMIN_PASSWORD` | 管理台密码 | 空 |
| `ONESSH_MASTER_KEY` | 凭据加密主密钥（64 位 hex） | 空 |
| `ONESSH_PUBLIC_URL` | 对外 URL（OAuth/图标用） | 空 |
| `ONESSH_MCP_LISTEN` | MCP HTTP 独立端口 | 主端口+1 |
| `ONESSH_DISABLED_TOOLS` | 实例级禁用工具组（逗号分隔） | 空 |
| `ONESSH_DATA_DIR` | 数据目录 | `data/` |

## 部署（Docker）

```bash
dotnet build SubSsh.slnx -c Release
dotnet publish src/core/SubSsh.Host -c Release -r linux-x64 --self-contained false -o release/app
cp -r src/core/SubSsh.Host/bin/Release/net10.0/plugins release/app/plugins
docker run -d --name subssh --restart unless-stopped \
  -v $(pwd)/release/app:/app -w /app \
  -e ONESSH_LISTEN=0.0.0.0:8080 \
  -e ONESSH_PUBLIC_URL=https://your.domain \
  -e ONESSH_ADMIN_PASSWORD=change-me \
  -p 127.0.0.1:8080:8080 \
  mcr.microsoft.com/dotnet/aspnet:10.0 dotnet SubSsh.Host.dll
```

## 与原版 OneSSH 的关系

功能语义、HTTP 路由、MCP 工具契约、数据库 schema（15 个 migration 原样沿用）与错误文案均以
[OneSSH](https://github.com/Lynricsy/OneSSH) 为蓝本 1:1 移植；语言从 Go 换为 C#，
架构重组为时空可组合插件范式，前端由 React 重写为 Vue 3。本产品是独立实现，与原作无隶属关系。

## 许可证

[GPL-3.0](LICENSE)（与原项目一致）
