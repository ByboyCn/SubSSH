<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref } from 'vue'
import { listHosts, listMetrics } from '@/api/client'
import type { Host, Metric } from '@/types'
import { useToastStore } from '@/stores/toast'

const toast = useToastStore()
const hosts = ref<Host[]>([])
const hostId = ref<number>()
const hours = ref(24)
const metrics = ref<Metric[]>([])
const loading = ref(false)
let timer: number | undefined

async function refresh() {
  if (hostId.value == null) return
  loading.value = true
  try {
    metrics.value = await listMetrics(hostId.value, hours.value)
  } catch (e) {
    toast.error(e)
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    hosts.value = await listHosts()
    if (hosts.value.length) {
      hostId.value = hosts.value[0].id
      await refresh()
    }
  } catch (e) {
    toast.error(e)
  }
  timer = window.setInterval(refresh, 60_000)
})

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
})

const latest = computed(() => metrics.value[metrics.value.length - 1])

/** 轻量条形图占位：正式曲线图后续可换 ECharts/uPlot */
const cpuBars = computed(() =>
  metrics.value.slice(-60).map((m) => ({
    ts: m.ts,
    cpu: m.cpuPct ?? 0,
    height: `${Math.min(100, m.cpuPct ?? 0)}%`,
  })),
)

const fmtKb = (kb?: number) => (kb == null ? '—' : `${(kb / (1 << 20)).toFixed(1)}GB`)
const fmtTs = (ts: number) => new Date(ts * 1000).toLocaleTimeString()
</script>

<template>
  <div class="space-y-4">
    <div class="flex items-center gap-2">
      <select v-model.number="hostId" class="input w-48" @change="refresh()">
        <option v-for="h in hosts" :key="h.id" :value="h.id">{{ h.name }}</option>
      </select>
      <select v-model.number="hours" class="input w-32" @change="refresh()">
        <option :value="1">最近 1 小时</option>
        <option :value="6">最近 6 小时</option>
        <option :value="24">最近 24 小时</option>
        <option :value="168">最近 7 天</option>
      </select>
      <span v-if="latest" class="text-sm text-muted">采样 {{ fmtTs(latest.ts) }}</span>
    </div>

    <div class="grid grid-cols-3 gap-4" v-if="latest">
      <div class="card">
        <div class="text-xs text-muted">CPU</div>
        <div class="text-2xl font-semibold">{{ latest.cpuPct?.toFixed(1) ?? '—' }}%</div>
      </div>
      <div class="card">
        <div class="text-xs text-muted">内存</div>
        <div class="text-2xl font-semibold">
          {{ fmtKb(latest.memUsedKb) }} <span class="text-sm text-muted">/ {{ fmtKb(latest.memTotalKb) }}</span>
        </div>
        <div class="mt-2 h-1.5 rounded bg-raised">
          <div
            class="h-1.5 rounded bg-accent"
            :style="{ width: `${latest.memUsedKb && latest.memTotalKb ? (100 * latest.memUsedKb) / latest.memTotalKb : 0}%` }"
          />
        </div>
      </div>
      <div class="card">
        <div class="text-xs text-muted">1 分钟负载</div>
        <div class="text-2xl font-semibold">{{ latest.load1?.toFixed(2) ?? '—' }}</div>
      </div>
    </div>

    <div class="card">
      <h2 class="mb-3 text-sm font-medium">CPU 历史（最近 60 个采样）</h2>
      <div class="flex h-32 items-end gap-0.5">
        <div
          v-for="b in cpuBars"
          :key="b.ts"
          class="flex-1 rounded-t bg-accent/70"
          :style="{ height: b.height }"
          :title="`${fmtTs(b.ts)}: ${b.cpu.toFixed(1)}%`"
        />
      </div>
    </div>

    <div class="card">
      <h2 class="mb-3 text-sm font-medium">磁盘</h2>
      <table class="table-base">
        <thead><tr><th>挂载点</th><th>已用</th><th>总量</th><th>占比</th></tr></thead>
        <tbody>
          <tr v-for="d in latest?.disks ?? []" :key="d.mount">
            <td class="font-mono text-xs">{{ d.mount }}</td>
            <td class="text-muted">{{ fmtKb(d.usedKb) }}</td>
            <td class="text-muted">{{ fmtKb(d.totalKb) }}</td>
            <td class="w-48">
              <div class="h-1.5 rounded bg-raised">
                <div
                  class="h-1.5 rounded"
                  :class="d.usedKb / d.totalKb > 0.9 ? 'bg-danger' : 'bg-ok'"
                  :style="{ width: `${(100 * d.usedKb) / d.totalKb}%` }"
                />
              </div>
            </td>
          </tr>
          <tr v-if="!latest?.disks?.length"><td colspan="4" class="text-center text-muted">暂无数据</td></tr>
        </tbody>
      </table>
    </div>
    <div v-if="loading" class="text-center text-sm text-muted">加载中…</div>
  </div>
</template>
