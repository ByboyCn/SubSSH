/**
 * fetch 封装：统一 /api/v1 前缀、JSON 编解码、错误消息提取与 401 广播。
 * 所有路径参数为 /api/v1 之后的相对路径（如 '/hosts'）。
 */

export type ApiError = Error & { status: number }

/** 401 广播：AuthGuard 监听后跳转登录页 */
export const UNAUTHORIZED_EVENT = 'subssh:unauthorized'

async function responseError(response: Response): Promise<ApiError> {
  let message = response.statusText
  try {
    const body = await response.json()
    message = body.error || message
  } catch {
    /* 非 JSON 错误体，保留 statusText */
  }
  const error = new Error(message) as ApiError
  error.status = response.status
  if (response.status === 401) window.dispatchEvent(new CustomEvent(UNAUTHORIZED_EVENT))
  return error
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch('/api/v1' + path, {
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    ...init,
  })
  if (!response.ok) throw await responseError(response)
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export const post = <T>(path: string, body: unknown) =>
  api<T>(path, { method: 'POST', body: JSON.stringify(body) })

export const put = <T>(path: string, body: unknown) =>
  api<T>(path, { method: 'PUT', body: JSON.stringify(body) })

export const del = <T>(path: string) => api<T>(path, { method: 'DELETE' })

/** 上传走 multipart，不能复用 JSON wrapper（会覆盖 boundary） */
export async function uploadFile(hostId: number, path: string, file: File): Promise<void> {
  const body = new FormData()
  body.append('file', file)
  body.append('path', path)
  const response = await fetch(`/api/v1/sftp/${hostId}/upload`, {
    method: 'POST',
    credentials: 'same-origin',
    body,
  })
  if (!response.ok) {
    if (response.status === 401) window.dispatchEvent(new CustomEvent(UNAUTHORIZED_EVENT))
    throw new Error((await response.text()) || response.statusText)
  }
}

/** SFTP 下载/预览直链 */
export const downloadUrl = (hostId: number, path: string) =>
  `/api/v1/sftp/${hostId}/download?path=${encodeURIComponent(path)}`

/** 下载前 HEAD 验证权限与远端文件可读性，不传输正文 */
export async function checkDownload(hostId: number, path: string): Promise<void> {
  const response = await fetch(downloadUrl(hostId, path), {
    method: 'HEAD',
    credentials: 'same-origin',
  })
  if (!response.ok) throw await responseError(response)
}

/* ── 领域 API（页面直接调用） ─────────────────────────────── */

import type {
  ApiToken,
  Audit,
  CommandOutputChunk,
  CommandRun,
  FileEntry,
  Host,
  HostPayload,
  JobStatus,
  KeyPayload,
  MemoryBankStat,
  MemoryRow,
  Metric,
  OAuthAuthorizationInfo,
  OAuthDecisionPayload,
  OAuthDecisionResult,
  SshKey,
  TokenPayload,
  ToolGroup,
} from '@/types'

export const listHosts = () => api<Host[]>('/hosts')
export const saveHost = (id: number | undefined, payload: HostPayload) =>
  id ? put<Host>(`/hosts/${id}`, payload) : post<Host>('/hosts', payload)
export const deleteHost = (id: number) => del<void>(`/hosts/${id}`)
export const testHost = (id: number) => post<{ output?: string }>(`/hosts/${id}/test`, {})
export const resetFingerprint = (id: number) => post<unknown>(`/hosts/${id}/reset-fingerprint`, {})

export const listKeys = () => api<SshKey[]>('/keys')
export const createKey = (payload: KeyPayload) => post<SshKey>('/keys', payload)
export const deleteKey = (id: number) => del<void>(`/keys/${id}`)

export const listTokens = () => api<ApiToken[]>('/tokens')
export const createToken = (payload: TokenPayload) => post<ApiToken>('/tokens', payload)
export const updateToken = (id: number, payload: TokenPayload) => put<ApiToken>(`/tokens/${id}`, payload)
export const deleteToken = (id: number) => del<void>(`/tokens/${id}`)
export const listToolGroups = () => api<ToolGroup[]>('/tool-groups')

export const listJobs = () => api<JobStatus[]>('/jobs')
export const jobLogs = (id: string, offsetBytes = 0, limitBytes = 128 << 10) =>
  api<CommandOutputChunk>(`/jobs/${id}/logs?offset_bytes=${offsetBytes}&limit_bytes=${limitBytes}`)
export const killJob = (id: string) => post<unknown>(`/jobs/${id}/kill`, {})

export type AuditFilter = { tool?: string[]; token?: number[]; host?: string[]; ok?: boolean }

export function listAudit(filter: AuditFilter = {}, limit = 100): Promise<Audit[]> {
  // 重复参数而不是逗号拼接：主机名允许含逗号，拼起来后端拆不回来
  const params = new URLSearchParams({ limit: String(limit) })
  for (const tool of filter.tool ?? []) params.append('tool', tool)
  for (const token of filter.token ?? []) params.append('token', String(token))
  for (const host of filter.host ?? []) params.append('host', host)
  if (filter.ok != null) params.set('ok', String(filter.ok))
  return api<Audit[]>(`/audit?${params}`)
}
export const listAuditTools = () => api<string[]>('/audit/tools')

export const commandRun = (id: string) => api<CommandRun>(`/command-runs/${id}`)
export function commandOutput(
  id: string,
  stream: 'stdout' | 'stderr' | 'combined',
  offsetBytes = 0,
  limitBytes = 128 << 10,
): Promise<CommandOutputChunk> {
  const params = new URLSearchParams({ stream, offset_bytes: String(offsetBytes), limit_bytes: String(limitBytes) })
  return api<CommandOutputChunk>(`/command-runs/${id}/output?${params}`)
}

export const listMetrics = (hostId: number, hours = 24) => api<Metric[]>(`/metrics/${hostId}?hours=${hours}`)

export const sftpList = (hostId: number, path: string) =>
  api<FileEntry[]>(`/sftp/${hostId}/list?path=${encodeURIComponent(path)}`)

export type MemoryFilter = { hostId?: number; q?: string; limit?: number; offset?: number }

export function listMemories({ hostId, q = '', limit = 50, offset = 0 }: MemoryFilter = {}) {
  const params = new URLSearchParams({ limit: String(limit), offset: String(offset) })
  if (hostId != null) params.set('host_id', String(hostId))
  if (q) params.set('q', q)
  return api<MemoryRow[]>(`/memories?${params}`)
}
export const memoryStats = () => api<MemoryBankStat[]>('/memories/stats')
export const deleteMemory = (id: number) => del<void>(`/memories/${id}`)

export const oauthAuthorization = (query: string) =>
  api<OAuthAuthorizationInfo>(`/oauth/authorization${query}`)
export const oauthDecision = (payload: OAuthDecisionPayload) =>
  post<OAuthDecisionResult>('/oauth/authorization', payload)
