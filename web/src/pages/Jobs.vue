<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { jobLogs, killJob, listJobs } from '@/api/client'
import type { JobStatus } from '@/types'
import { useToastStore } from '@/stores/toast'

const toast = useToastStore()
const jobs = ref<JobStatus[]>([])
const selected = ref<JobStatus | null>(null)
const logs = ref('')
const loading = ref(true)
let poll: number | undefined
let logPoll: number | undefined

async function refresh() {
  try {
    jobs.value = await listJobs()
    // 轮询时保持当前选中行跟随最新快照
    if (selected.value) {
      selected.value = jobs.value.find((j) => j.job.id === selected.value!.job.id) ?? null
    }
  } catch (e) {
    toast.error(e)
  } finally {
    loading.value = false
  }
}

async function loadLogs() {
  const job = selected.value
  if (!job) return
  try {
    // 增量读取：从已读字节继续，而不是每次拉全量
    const chunk = await jobLogs(job.job.id, logs.value.length, 128 << 10)
    if (chunk.content) logs.value += chunk.content
  } catch (e) {
    toast.error(e)
    if (logPoll) window.clearInterval(logPoll)
  }
}

function select(job: JobStatus) {
  selected.value = job
  logs.value = ''
  if (logPoll) window.clearInterval(logPoll)
  void loadLogs()
  if (job.job.status === 'running') logPoll = window.setInterval(loadLogs, 2000)
}

async function kill(job: JobStatus) {
  try {
    await killJob(job.job.id)
    toast.success('已发送终止信号')
    await refresh()
    if (selected.value?.job.id === job.job.id) select(jobs.value.find((j) => j.job.id === job.job.id) ?? job)
  } catch (e) {
    toast.error(e)
  }
}

onMounted(() => {
  void refresh()
  poll = window.setInterval(refresh, 4000)
})

onBeforeUnmount(() => {
  if (poll) window.clearInterval(poll)
  if (logPoll) window.clearInterval(logPoll)
})

const fmtTs = (ts: number | null) => (ts == null ? '—' : new Date(ts * 1000).toLocaleString())
const statusClass = (s: string) =>
  s === 'running' ? 'bg-accent/15 text-accent' : s === 'succeeded' ? 'bg-ok/15 text-ok' : 'bg-danger/15 text-danger'
</script>

<template>
  <div class="grid grid-cols-[1fr_480px] gap-4">
    <div class="card overflow-x-auto">
      <table class="table-base">
        <thead>
          <tr><th>ID</th><th>命令</th><th>状态</th><th>退出码</th><th>开始</th><th>结束</th><th class="w-32">操作</th></tr>
        </thead>
        <tbody>
          <tr v-for="j in jobs" :key="j.job.id" class="cursor-pointer hover:bg-raised" @click="select(j)">
            <td class="font-mono text-xs">{{ j.job.id.slice(0, 8) }}</td>
            <td class="max-w-xs truncate font-mono text-xs" :title="j.job.command">{{ j.job.command }}</td>
            <td><span class="badge" :class="statusClass(j.job.status)">{{ j.job.status }}</span></td>
            <td>{{ j.job.exitCode ?? '—' }}</td>
            <td class="text-muted text-xs">{{ fmtTs(j.job.startedAt) }}</td>
            <td class="text-muted text-xs">{{ fmtTs(j.job.finishedAt) }}</td>
            <td>
              <button v-if="j.job.status === 'running'" class="btn btn-danger" @click.stop="kill(j)">终止</button>
            </td>
          </tr>
          <tr v-if="!loading && jobs.length === 0">
            <td colspan="7" class="text-center text-muted">暂无后台任务</td>
          </tr>
        </tbody>
      </table>
      <div v-if="loading" class="py-4 text-center text-sm text-muted">加载中…</div>
    </div>

    <div class="card flex flex-col">
      <h2 class="mb-2 text-sm font-medium">任务日志</h2>
      <template v-if="selected">
        <div class="mb-2 text-xs text-muted">
          <span class="font-mono">{{ selected.job.command }}</span>
          · cwd <span class="font-mono">{{ selected.job.cwd }}</span>
        </div>
        <p v-if="selected.refreshError" class="mb-2 text-xs text-warn">刷新状态失败：{{ selected.refreshError }}</p>
        <pre class="max-h-96 flex-1 overflow-auto rounded bg-bg p-2 font-mono text-xs whitespace-pre-wrap">{{ logs || '(无日志)' }}</pre>
      </template>
      <p v-else class="text-sm text-muted">点击左侧任务查看日志</p>
    </div>
  </div>
</template>
