/**
 * 后端 REST 契约类型（SubSsh.Plugin.WebApi /api/v1）。
 * 字段为 camelCase（System.Text.Json 默认序列化），形状对齐 SubSSH 原版 web/src/api/types.ts。
 */

export type AuthType = 'key' | 'password'

export type Host = {
  id: number
  name: string
  addr: string
  port: number
  username: string
  authType: AuthType
  keyId?: number
  /** 已固定的远端主机公钥指纹（TOFU） */
  hostKeyFp?: string
  /** 跳板主机 id；缺省表示直连 */
  jumpHostId?: number
  monitorEnabled: boolean
  /** 已归档：保留配置与历史，但退出监控与 MCP 访问；列表接口照常返回 */
  archived: boolean
  /** 分组标签，后端保证非 null */
  tags: string[]
  createdAt: number
}

export type SshKey = {
  id: number
  name: string
  publicKey: string
  createdAt: number
}

export type ApiToken = {
  id: number
  name: string
  allHosts: boolean
  manageHosts: boolean
  /** MCP 工具组 denylist */
  disabledTools: string[]
  hostIds?: number[]
  createdAt: number
  /** 仅创建响应携带的一次性明文 */
  token?: string
}

export type JobRecord = {
  id: string
  hostId: number
  command: string
  cwd: string
  status: string
  exitCode: number | null
  startedAt: number
  finishedAt: number | null
}

export type JobStatus = {
  job: JobRecord
  logBytes: number
  /** 本次没能登录主机确认状态时出现，此时 job.status 只是数据库里的上次快照 */
  refreshError?: string
}

export type DiskUsage = { mount: string; usedKb: number; totalKb: number }

export type Metric = {
  hostId: number
  ts: number
  cpuPct?: number
  memUsedKb?: number
  memTotalKb?: number
  load1?: number
  disks: DiskUsage[]
}

export type FileEntry = {
  name: string
  size: number
  mode: string
  mtime: number
  directory: boolean
  symlinkTarget?: string
}

/** 可空标量沿用 Go/SQL null 语义的三值结构 */
export type Nullable<T> = { int64?: T; string?: T; valid: boolean }

export type Audit = {
  id: number
  ts: number
  tokenId: Nullable<number>
  tokenName: Nullable<string>
  tool: string
  host: Nullable<string>
  paramsJson: string
  runIds?: string[]
  ok: boolean
  durationMs: number
  bytesOut: number
}

export type CommandRunStatus =
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'timeout'
  | 'cancelled'
  | 'lost'

export type CommandRun = {
  seq: number
  id: string
  tokenId: number | null
  tokenName: string | null
  tool: string
  hostId: number | null
  host: string
  command: string
  cwd: string
  session: string | null
  jobId: string | null
  status: CommandRunStatus
  exitCode: number | null
  stdoutPreview?: string
  stderrPreview?: string
  stdoutBytes: number
  stderrBytes: number
  outputAvailable: boolean
  outputExpired: boolean
  outputError: string | null
  error: string | null
  startedAt: number
  finishedAt: number | null
  durationMs: number
}

export type CommandOutputChunk = {
  content: string
  offsetBytes: number
  nextOffsetBytes: number
  totalBytes: number
  complete: boolean
}

/** 记忆行：hostId 为 null 即全局记忆库；hostName 由后端 LEFT JOIN 带出 */
export type MemoryRow = {
  id: number
  hostId: number | null
  hostName: string | null
  content: string
  source: string
  /** 0–1 连续值 */
  importance: number
  /** stated | inferred | tool | unknown */
  veracity: string
  createdAt: number
  updatedAt: number
  recallCount: number
}

/** 按记忆库聚合的统计；hostId 为 null 即全局库 */
export type MemoryBankStat = {
  hostId: number | null
  hostName: string | null
  count: number
  /** 已生成向量的条数，未配置 embedding 时恒为 0 */
  embedded: number
  lastWritten: number | null
}

/** SSE 事件流载荷 */
export type StreamEvent = {
  type: string
  ts: number
  data: unknown
}

/* ── 表单载荷 ─────────────────────────────────────────────── */

export type HostPayload = {
  name: string
  username: string
  addr: string
  port: number
  authType: AuthType
  keyId?: number
  password?: string
  /** 跳板主机名；空/省略表示直连 */
  jumpHost?: string
  monitorEnabled: boolean
  tags?: string[]
}

export type KeyPayload = {
  name: string
  mode: 'generate' | 'import'
  privateKey?: string
  /** 仅导入受口令保护的私钥时提交；服务端解密后丢弃，不落库 */
  passphrase?: string
}

export type TokenPayload = {
  name: string
  allHosts: boolean
  manageHosts: boolean
  hostIds?: number[]
  disabledTools?: string[]
}

export type ToolGroup = {
  name: string
  tools: string[]
}

/** GET /oauth/authorization?<原始查询串> */
export type OAuthAuthorizationInfo = {
  clientName: string
  clientUri?: string
  redirectUri: string
  requestedScopes: string[]
  hosts: Host[]
  toolGroups: ToolGroup[]
}

/** POST /oauth/authorization，query 必须是未经改写的原始查询串 */
export type OAuthDecisionPayload = {
  query: string
  decision: 'approve' | 'deny'
  allHosts: boolean
  manageHosts: boolean
  hostIds?: number[]
  disabledTools?: string[]
}

/** 无论批准还是拒绝，后端都给出下一跳；拒绝时带 error=access_denied */
export type OAuthDecisionResult = { redirectUri: string }
