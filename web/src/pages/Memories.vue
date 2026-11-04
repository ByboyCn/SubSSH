<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { deleteMemory, listHosts, listMemories, memoryStats } from '@/api/client'
import type { Host, MemoryBankStat, MemoryRow } from '@/types'
import { useToastStore } from '@/stores/toast'

const toast = useToastStore()
const memories = ref<MemoryRow[]>([])
const stats = ref<MemoryBankStat[]>([])
const hosts = ref<Host[]>([])
const q = ref('')
const hostId = ref<number | 'all'>('all')
const loading = ref(true)

async function refresh() {
  loading.value = true
  try {
    memories.value = await listMemories({ hostId: hostId.value === 'all' ? undefined : hostId.value, q: q.value })
  } catch (e) {
    toast.error(e)
  } finally {
    loading.value = false
  }
}

async function remove(m: MemoryRow) {
  if (!window.confirm('确定删除这条记忆？此操作不可撤销。')) return
  try {
    await deleteMemory(m.id)
    toast.success('记忆已删除')
    await Promise.all([refresh(), loadStats()])
  } catch (e) {
    toast.error(e)
  }
}

async function loadStats() {
  try {
    stats.value = await memoryStats()
  } catch (e) {
    toast.error(e)
  }
}

onMounted(async () => {
  try {
    hosts.value = await listHosts()
  } catch (e) {
    toast.error(e)
  }
  await Promise.all([refresh(), loadStats()])
})

const bankName = (s: MemoryBankStat) => (s.hostId === null ? '全局' : (s.hostName ?? `#${s.hostId}`))
const fmtTs = (ts: number) => new Date(ts * 1000).toLocaleString()
</script>

<template>
  <div class="grid grid-cols-[1fr_300px] gap-4">
    <div class="space-y-4">
      <div class="flex gap-2">
        <select v-model="hostId" class="input w-48" @change="refresh()">
          <option value="all">全部记忆库</option>
          <option :value="null">全局库</option>
          <option v-for="h in hosts" :key="h.id" :value="h.id">{{ h.name }}</option>
        </select>
        <input v-model="q" class="input flex-1" placeholder="搜索记忆…" @keydown.enter="refresh()" />
        <button class="btn btn-primary" @click="refresh()">搜索</button>
      </div>

      <div class="space-y-2">
        <div v-for="m in memories" :key="m.id" class="card">
          <div class="mb-1 flex items-center gap-2 text-xs text-muted">
            <span class="badge bg-raised">{{ m.hostName ?? '全局' }}</span>
            <span class="badge bg-raised">{{ m.veracity }}</span>
            <span class="badge bg-raised">重要度 {{ m.importance.toFixed(2) }}</span>
            <span class="badge bg-raised">召回 {{ m.recallCount }}</span>
            <span class="ml-auto">{{ fmtTs(m.updatedAt) }}</span>
            <button class="btn btn-danger !px-2 !py-0.5" @click="remove(m)">删除</button>
          </div>
          <p class="text-sm">{{ m.content }}</p>
        </div>
        <div v-if="!loading && memories.length === 0" class="card text-center text-muted">无匹配记忆</div>
        <div v-if="loading" class="card text-center text-sm text-muted">加载中…</div>
      </div>
    </div>

    <div class="card">
      <h2 class="mb-3 text-sm font-medium">记忆库统计</h2>
      <table class="table-base">
        <thead><tr><th>库</th><th>条数</th><th>已向量化</th></tr></thead>
        <tbody>
          <tr v-for="(s, i) in stats" :key="i">
            <td>{{ bankName(s) }}</td>
            <td>{{ s.count }}</td>
            <td class="text-muted">{{ s.embedded }}</td>
          </tr>
          <tr v-if="stats.length === 0"><td colspan="3" class="text-center text-muted">暂无数据</td></tr>
        </tbody>
      </table>
    </div>
  </div>
</template>
