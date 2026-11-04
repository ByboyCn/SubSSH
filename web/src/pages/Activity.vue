<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { listAudit, listAuditTools } from '@/api/client'
import type { Audit } from '@/types'
import { useToastStore } from '@/stores/toast'

const toast = useToastStore()
const audit = ref<Audit[]>([])
const tools = ref<string[]>([])
const loading = ref(true)

const filter = reactive<{ tool: string[]; ok: boolean | null }>({ tool: [], ok: null })

async function refresh() {
  loading.value = true
  try {
    audit.value = await listAudit({ tool: filter.tool.length ? filter.tool : undefined, ok: filter.ok ?? undefined })
  } catch (e) {
    toast.error(e)
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    // 工具全集来自独立端点：审计列表只回最近 100 条，累积出的选项会漏更早的工具
    tools.value = await listAuditTools()
  } catch (e) {
    toast.error(e)
  }
  await refresh()
})

function toggleTool(t: string) {
  filter.tool = filter.tool.includes(t) ? filter.tool.filter((x) => x !== t) : [...filter.tool, t]
  void refresh()
}

function setOk(v: boolean | null) {
  filter.ok = v
  void refresh()
}

const fmtTs = (ts: number) => new Date(ts * 1000).toLocaleString()
</script>

<template>
  <div class="space-y-4">
    <div class="flex flex-wrap items-center gap-2">
      <div class="flex overflow-hidden rounded-md border border-border">
        <button class="px-3 py-1.5 text-sm" :class="filter.ok === null ? 'bg-accent/15 text-accent' : 'text-muted'" @click="setOk(null)">全部</button>
        <button class="px-3 py-1.5 text-sm" :class="filter.ok === true ? 'bg-ok/15 text-ok' : 'text-muted'" @click="setOk(true)">成功</button>
        <button class="px-3 py-1.5 text-sm" :class="filter.ok === false ? 'bg-danger/15 text-danger' : 'text-muted'" @click="setOk(false)">失败</button>
      </div>
      <div class="flex flex-wrap gap-1">
        <button
          v-for="t in tools"
          :key="t"
          class="badge font-mono"
          :class="filter.tool.includes(t) ? 'bg-accent/15 text-accent' : 'bg-raised text-muted'"
          @click="toggleTool(t)"
        >
          {{ t }}
        </button>
      </div>
    </div>

    <div class="card overflow-x-auto">
      <table class="table-base">
        <thead>
          <tr><th>时间</th><th>令牌</th><th>工具</th><th>主机</th><th>参数</th><th>结果</th><th>耗时</th><th>输出</th></tr>
        </thead>
        <tbody>
          <tr v-for="a in audit" :key="a.id">
            <td class="text-muted text-xs">{{ fmtTs(a.ts) }}</td>
            <td>{{ a.tokenName.valid ? a.tokenName.string : '—' }}</td>
            <td class="font-mono text-xs">{{ a.tool }}</td>
            <td>{{ a.host.valid ? a.host.string : '—' }}</td>
            <td class="max-w-xs truncate font-mono text-xs text-muted" :title="a.paramsJson">{{ a.paramsJson }}</td>
            <td>
              <span class="badge" :class="a.ok ? 'bg-ok/15 text-ok' : 'bg-danger/15 text-danger'">{{ a.ok ? '成功' : '失败' }}</span>
            </td>
            <td class="text-muted">{{ a.durationMs }}ms</td>
            <td class="text-muted text-xs">{{ (a.bytesOut / 1024).toFixed(1) }}KB</td>
          </tr>
          <tr v-if="!loading && audit.length === 0">
            <td colspan="8" class="text-center text-muted">暂无活动记录</td>
          </tr>
        </tbody>
      </table>
      <div v-if="loading" class="py-4 text-center text-sm text-muted">加载中…</div>
    </div>
  </div>
</template>
