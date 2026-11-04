<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { listAudit, listHosts, listJobs, listTokens } from '@/api/client'
import type { Audit, Host, JobStatus } from '@/types'

const hosts = ref<Host[]>([])
const tokens = ref<{ id: number; name: string }[]>([])
const recentAudit = ref<Audit[]>([])
const runningJobs = ref<JobStatus[]>([])
const loading = ref(true)
const error = ref('')

onMounted(async () => {
  try {
    const [h, t, a, j] = await Promise.all([listHosts(), listTokens(), listAudit({}, 20), listJobs()])
    hosts.value = h
    tokens.value = t.map(({ id, name }) => ({ id, name }))
    recentAudit.value = a
    runningJobs.value = j.filter((s) => s.job.status === 'running')
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
})

const fmtTs = (ts: number) => new Date(ts * 1000).toLocaleString()
</script>

<template>
  <div class="space-y-6">
    <p v-if="error" class="text-sm text-danger">{{ error }}</p>
    <div v-else-if="loading" class="text-sm text-muted">加载中…</div>

    <template v-else>
      <div class="grid grid-cols-4 gap-4">
        <div class="card">
          <div class="text-xs text-muted">主机</div>
          <div class="text-2xl font-semibold">{{ hosts.filter((h) => !h.archived).length }}</div>
        </div>
        <div class="card">
          <div class="text-xs text-muted">已归档主机</div>
          <div class="text-2xl font-semibold">{{ hosts.filter((h) => h.archived).length }}</div>
        </div>
        <div class="card">
          <div class="text-xs text-muted">令牌</div>
          <div class="text-2xl font-semibold">{{ tokens.length }}</div>
        </div>
        <div class="card">
          <div class="text-xs text-muted">运行中的任务</div>
          <div class="text-2xl font-semibold text-ok">{{ runningJobs.length }}</div>
        </div>
      </div>

      <div class="card">
        <h2 class="mb-3 text-sm font-medium">最近工具调用（最近 20 条）</h2>
        <table class="table-base">
          <thead>
            <tr>
              <th>时间</th><th>令牌</th><th>工具</th><th>主机</th><th>结果</th><th>耗时</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="a in recentAudit" :key="a.id">
              <td class="text-muted">{{ fmtTs(a.ts) }}</td>
              <td>{{ a.tokenName.valid ? a.tokenName.string : '—' }}</td>
              <td class="font-mono">{{ a.tool }}</td>
              <td>{{ a.host.valid ? a.host.string : '—' }}</td>
              <td>
                <span class="badge" :class="a.ok ? 'bg-ok/15 text-ok' : 'bg-danger/15 text-danger'">
                  {{ a.ok ? '成功' : '失败' }}
                </span>
              </td>
              <td class="text-muted">{{ a.durationMs }}ms</td>
            </tr>
            <tr v-if="recentAudit.length === 0">
              <td colspan="6" class="text-center text-muted">暂无活动记录</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </div>
</template>
