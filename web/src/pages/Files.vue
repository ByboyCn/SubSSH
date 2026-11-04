<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { checkDownload, downloadUrl, sftpList, uploadFile } from '@/api/client'
import type { FileEntry, Host } from '@/types'
import { listHosts } from '@/api/client'
import { useToastStore } from '@/stores/toast'

const toast = useToastStore()
const hosts = ref<Host[]>([])
const hostId = ref<number>()
const path = ref('/')
const entries = ref<FileEntry[]>([])
const loading = ref(false)
const selected = ref<File[]>([])
const uploadInput = ref<HTMLInputElement>()

onMounted(async () => {
  try {
    hosts.value = await listHosts()
    if (hosts.value.length) hostId.value = hosts.value[0].id
  } catch (e) {
    toast.error(e)
  }
})

const parent = computed(() => {
  const p = path.value.replace(/\/+$/, '')
  const idx = p.lastIndexOf('/')
  return idx <= 0 ? '/' : p.slice(0, idx)
})

async function browse(target?: string) {
  if (target != null) path.value = target
  if (hostId.value == null) return
  loading.value = true
  try {
    entries.value = await sftpList(hostId.value, path.value)
  } catch (e) {
    toast.error(e)
    entries.value = []
  } finally {
    loading.value = false
  }
}

function openEntry(e: FileEntry) {
  if (e.directory) void browse(path.value.replace(/\/$/, '') + '/' + e.name)
}

async function upload() {
  const file = selected.value[0]
  if (hostId.value == null || !file) {
    toast.warning('请先选择主机与文件')
    return
  }
  try {
    await uploadFile(hostId.value, path.value.replace(/\/$/, '') + '/', file)
    toast.success('上传完成')
    selected.value = []
    if (uploadInput.value) uploadInput.value.value = ''
    await browse()
  } catch (e) {
    toast.error(e)
  }
}

async function download(e: FileEntry) {
  if (hostId.value == null) return
  const full = path.value.replace(/\/$/, '') + '/' + e.name
  try {
    await checkDownload(hostId.value, full)
    window.location.assign(downloadUrl(hostId.value, full))
  } catch (err) {
    toast.error(err)
  }
}

const fmtSize = (n: number) => (n >= 1 << 20 ? `${(n / (1 << 20)).toFixed(1)}MB` : `${(n / 1024).toFixed(1)}KB`)
const fmtTime = (ts: number) => new Date(ts * 1000).toLocaleString()
</script>

<template>
  <div class="space-y-4">
    <div class="flex items-center gap-2">
      <select v-model.number="hostId" class="input w-48" @change="browse()">
        <option v-for="h in hosts" :key="h.id" :value="h.id">{{ h.name }}</option>
      </select>
      <input
        v-model="path"
        class="input flex-1 font-mono text-xs"
        placeholder="/var/log"
        @keydown.enter="browse()"
      />
      <button class="btn btn-primary" @click="browse()">浏览</button>
    </div>

    <div class="flex items-center gap-2">
      <input ref="uploadInput" type="file" class="text-sm" @change="selected = Array.from(($event.target as HTMLInputElement).files ?? [])" />
      <button class="btn" :disabled="!selected.length" @click="upload">上传到当前目录</button>
    </div>

    <div class="card overflow-x-auto">
      <div class="mb-2">
        <button class="btn" :disabled="path === '/'" @click="browse(parent)">↑ 上一级</button>
      </div>
      <table class="table-base">
        <thead>
          <tr><th>名称</th><th>大小</th><th>权限</th><th>修改时间</th><th class="w-28">操作</th></tr>
        </thead>
        <tbody>
          <tr v-for="e in entries" :key="e.name">
            <td>
              <button class="text-left hover:text-accent" @click="openEntry(e)">
                {{ e.directory ? '📁' : e.symlinkTarget ? '🔗' : '📄' }} {{ e.name }}
              </button>
            </td>
            <td class="text-muted">{{ e.directory ? '—' : fmtSize(e.size) }}</td>
            <td class="font-mono text-xs text-muted">{{ e.mode }}</td>
            <td class="text-muted text-xs">{{ fmtTime(e.mtime) }}</td>
            <td>
              <button v-if="!e.directory" class="btn" @click="download(e)">下载</button>
            </td>
          </tr>
          <tr v-if="!loading && entries.length === 0">
            <td colspan="5" class="text-center text-muted">目录为空或无法读取</td>
          </tr>
        </tbody>
      </table>
      <div v-if="loading" class="py-4 text-center text-sm text-muted">读取中…</div>
    </div>
  </div>
</template>
