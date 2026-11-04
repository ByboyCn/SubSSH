<script setup lang="ts">
import { reactive, ref } from 'vue'
import { createToken, deleteToken, listTokens, listToolGroups, updateToken } from '@/api/client'
import type { ApiToken, ToolGroup, TokenPayload } from '@/types'
import { useToastStore } from '@/stores/toast'

const toast = useToastStore()
const tokens = ref<ApiToken[]>([])
const groups = ref<ToolGroup[]>([])
const hosts = ref<{ id: number; name: string }[]>([])
const loading = ref(true)
const submitting = ref(false)
const freshToken = ref<{ name: string; value: string } | null>(null)

async function refresh() {
  loading.value = true
  try {
    const [t, g, h] = await Promise.all([listTokens(), listToolGroups(), import('@/api/client').then((m) => m.listHosts())])
    tokens.value = t
    groups.value = g
    hosts.value = h.map(({ id, name }) => ({ id, name }))
  } catch (e) {
    toast.error(e)
  } finally {
    loading.value = false
  }
}

// hostIds/disabledTools 在表单内始终持有数组，模板处无需可空判断
const emptyForm = () => ({ name: '', allHosts: true, manageHosts: false, hostIds: [] as number[], disabledTools: [] as string[] })
const form = reactive(emptyForm())
const editingId = ref<number | undefined>()

function openCreate() {
  editingId.value = undefined
  Object.assign(form, emptyForm())
}

function openEdit(t: ApiToken) {
  editingId.value = t.id
  Object.assign(form, {
    name: t.name,
    allHosts: t.allHosts,
    manageHosts: t.manageHosts,
    hostIds: [...(t.hostIds ?? [])],
    disabledTools: [...t.disabledTools],
  })
}

async function submit() {
  submitting.value = true
  try {
    const payload: TokenPayload = {
      name: form.name,
      allHosts: form.allHosts,
      manageHosts: form.manageHosts,
      hostIds: form.allHosts ? undefined : (form.hostIds ?? []),
      disabledTools: form.disabledTools,
    }
    if (editingId.value) {
      await updateToken(editingId.value, payload)
      toast.success('令牌已更新')
    } else {
      const created = await createToken(payload)
      if (created.token) freshToken.value = { name: created.name, value: created.token }
      toast.success('令牌已创建')
    }
    openCreate()
    await refresh()
  } catch (e) {
    toast.error(e)
  } finally {
    submitting.value = false
  }
}

async function remove(t: ApiToken) {
  if (!window.confirm(`确定删除令牌「${t.name}」？使用它的客户端会立即失去访问。`)) return
  try {
    await deleteToken(t.id)
    toast.success('令牌已删除')
    await refresh()
  } catch (e) {
    toast.error(e)
  }
}

const fmtTs = (ts: number) => new Date(ts * 1000).toLocaleString()

function toggleGroup(g: ToolGroup) {
  const all = g.tools.every((t) => form.disabledTools!.includes(t))
  form.disabledTools = all
    ? form.disabledTools!.filter((t) => !g.tools.includes(t))
    : [...new Set([...form.disabledTools!, ...g.tools])]
}

async function copyToken(text: string) {
  await navigator.clipboard.writeText(text)
  toast.success('已复制')
}
</script>

<template>
  <div class="grid grid-cols-[1fr_380px] gap-4">
    <div class="space-y-4">
      <div v-if="freshToken" class="card border-ok/40">
        <div class="mb-1 text-sm font-medium text-ok">令牌「{{ freshToken.name }}」已创建 — 明文仅显示这一次</div>
        <div class="flex items-center gap-2">
          <code class="flex-1 break-all rounded bg-bg px-2 py-1.5 font-mono text-xs">{{ freshToken.value }}</code>
          <button class="btn" @click="copyToken(freshToken.value)">复制</button>
        </div>
      </div>

      <div class="card overflow-x-auto">
        <table class="table-base">
          <thead>
            <tr><th>名称</th><th>范围</th><th>管理主机</th><th>主机限定</th><th>禁用工具</th><th>创建时间</th><th class="w-28">操作</th></tr>
          </thead>
          <tbody>
            <tr v-for="t in tokens" :key="t.id">
              <td class="font-medium">{{ t.name }}</td>
              <td>
                <span class="badge" :class="t.allHosts ? 'bg-accent/15 text-accent' : 'bg-raised text-muted'">
                  {{ t.allHosts ? '全部主机' : `指定 ${t.hostIds?.length ?? 0} 台` }}
                </span>
              </td>
              <td>{{ t.manageHosts ? '✓' : '—' }}</td>
              <td class="text-muted text-xs">{{ t.allHosts ? '—' : (t.hostIds ?? []).map((id) => hosts.find((h) => h.id === id)?.name ?? `#${id}`).join(', ') }}</td>
              <td class="text-muted text-xs">{{ t.disabledTools.length || '—' }}</td>
              <td class="text-muted text-xs">{{ fmtTs(t.createdAt) }}</td>
              <td>
                <div class="flex gap-1.5">
                  <button class="btn" @click="openEdit(t)">编辑</button>
                  <button class="btn btn-danger" @click="remove(t)">删除</button>
                </div>
              </td>
            </tr>
            <tr v-if="!loading && tokens.length === 0">
              <td colspan="7" class="text-center text-muted">暂无令牌</td>
            </tr>
          </tbody>
        </table>
        <div v-if="loading" class="py-4 text-center text-sm text-muted">加载中…</div>
      </div>
    </div>

    <!-- 创建 / 编辑令牌：名称、范围、管理权限、主机白名单、工具组勾选 -->
    <div class="card">
      <h2 class="mb-4 text-sm font-medium">{{ editingId ? '编辑令牌' : '创建令牌' }}</h2>
      <form class="space-y-3" @submit.prevent="submit">
        <div>
          <label class="label">名称</label>
          <input v-model="form.name" required class="input" placeholder="claude-code" />
        </div>
        <label class="flex items-center gap-2 text-sm">
          <input v-model="form.allHosts" type="checkbox" class="accent-accent" /> 可访问全部主机
        </label>
        <div v-if="!form.allHosts">
          <label class="label">主机白名单</label>
          <div class="max-h-40 space-y-1 overflow-y-auto rounded-md border border-border p-2">
            <label v-for="h in hosts" :key="h.id" class="flex items-center gap-2 text-sm">
              <input v-model="form.hostIds" type="checkbox" :value="h.id" class="accent-accent" />
              {{ h.name }}
            </label>
            <p v-if="hosts.length === 0" class="text-xs text-muted">暂无主机</p>
          </div>
        </div>
        <label class="flex items-center gap-2 text-sm">
          <input v-model="form.manageHosts" type="checkbox" class="accent-accent" /> 允许管理主机（增删改、指纹重置）
        </label>
        <div>
          <label class="label">禁用的 MCP 工具组</label>
          <div class="space-y-2">
            <div v-for="g in groups" :key="g.name" class="rounded-md border border-border p-2">
              <label class="flex items-center gap-2 text-sm font-medium">
                <input
                  type="checkbox"
                  class="accent-accent"
                  :checked="g.tools.every((t) => form.disabledTools.includes(t))"
                  :indeterminate="g.tools.some((t) => form.disabledTools.includes(t)) && !g.tools.every((t) => form.disabledTools.includes(t))"
                  @change="toggleGroup(g)"
                />
                {{ g.name }}
              </label>
              <div class="mt-1 flex flex-wrap gap-1 pl-6">
                <span v-for="t in g.tools" :key="t" class="badge font-mono" :class="form.disabledTools.includes(t) ? 'bg-danger/15 text-danger' : 'bg-raised text-muted'">
                  {{ t }}
                </span>
              </div>
            </div>
            <p v-if="groups.length === 0" class="text-xs text-muted">后端未提供工具组</p>
          </div>
        </div>
        <div class="flex justify-end gap-2">
          <button type="button" class="btn" @click="openCreate">重置</button>
          <button type="submit" class="btn btn-primary" :disabled="submitting">
            {{ submitting ? '提交中…' : editingId ? '保存' : '创建' }}
          </button>
        </div>
      </form>
    </div>
  </div>
</template>
