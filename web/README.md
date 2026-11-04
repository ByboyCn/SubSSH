# SubSsh Web 前端

Vue 3 + TypeScript + Vite + Pinia + Vue Router + Tailwind。对应 SubSsh.Plugin.WebApi（REST `/api/v1` + SSE `/api/events` + WebSocket 终端）。

## 启动

```bash
cd web
npm install
npm run dev        # http://localhost:5173，需先启动后端（SubSsh.Host）
```

dev 服务器已代理：

| 前缀 | 目标 |
|---|---|
| `/api`（含 WebSocket/SSE） | `http://127.0.0.1:8080` |
| `/.well-known`（OAuth 元数据） | `http://127.0.0.1:8080` |

## 构建

```bash
npm run build
```

产物输出到 `../src/SubSsh.Plugin.WebApi/dist`，由 WebApi 插件直接托管 SPA（无需拷贝）。

## 目录结构

- `src/api/client.ts` — fetch 封装（`/api/v1` 前缀、JSON 编解码、401 广播）与全部领域 API 函数
- `src/types.ts` — 后端契约类型（camelCase，对齐 `SubSsh.Core.Models`）
- `src/components/layout/` — 深色侧边栏、顶栏、布局与导航定义（`nav-items.ts`）
- `src/pages/` — dashboard / hosts / keys / tokens / terminal / files / jobs / activity / metrics / memories / login / oauth-authorize / not-found
- `src/stores/` — Pinia：auth（会话与 401 处理）、toast（提示队列）

## 页面要点

- **主机**：创建/编辑/删除/连接测试/重置 TOFU 指纹表单，标签、跳板、归档过滤
- **令牌**：创建时展示一次性明文，主机白名单与 MCP 工具组勾选
- **终端**：`@xterm/xterm` 动态加载，WebSocket `/api/v1/terminal?host_id=`
- **任务**：4s 轮询列表 + 2s 增量日志（`offset_bytes` 续读）
- **文件**：SFTP 浏览/上传/下载（下载前 HEAD 校验）

## 约定

- 类型字段与后端 JSON（System.Text.Json camelCase）一致，禁止改名
- 错误统一走 `useToastStore().error(e)`；401 由 `api/client.ts` 广播 `subssh:unauthorized`，auth store 负责跳登录
