<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { listHosts } from '@/api/client'
import type { Host } from '@/types'
import { useToastStore } from '@/stores/toast'

const toast = useToastStore()
const hosts = ref<Host[]>([])
const hostId = ref<number>()
const connected = ref(false)
const container = ref<HTMLDivElement>()

let term: import('@xterm/xterm').Terminal | undefined
let fitAddon: import('@xterm/addon-fit').FitAddon | undefined
let ws: WebSocket | undefined

function wsUrl(id: number): string {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  return `${proto}://${location.host}/api/v1/terminal?host_id=${id}`
}

async function connect() {
  if (hostId.value == null) return
  disconnect()

  // xterm 按需加载：终端页是少数访客才会打开的页面
  const [{ Terminal }, { FitAddon }] = await Promise.all([import('@xterm/xterm'), import('@xterm/addon-fit')])
  term = new Terminal({
    cursorBlink: true,
    fontSize: 13,
    fontFamily: '"JetBrains Mono", ui-monospace, Menlo, monospace',
    theme: { background: '#0b0e14', foreground: '#e6eaf2' },
  })
  fitAddon = new FitAddon()
  term.loadAddon(fitAddon)
  if (container.value) {
    term.open(container.value)
    fitAddon.fit()
  }
  term.onData((data) => ws?.send(data))

  try {
    ws = new WebSocket(wsUrl(hostId.value))
  } catch (e) {
    toast.error(e)
    return
  }
  ws.onopen = () => {
    connected.value = true
    term?.writeln(`\x1b[32m已连接到 ${hosts.value.find((h) => h.id === hostId.value)?.name ?? hostId.value}\x1b[0m`)
    term?.focus()
  }
  ws.onmessage = (ev) => term?.write(ev.data as string)
  ws.onclose = () => {
    connected.value = false
    term?.writeln('\r\n\x1b[31m连接已关闭\x1b[0m')
  }
  ws.onerror = () => toast.error(new Error('WebSocket 连接失败'))
}

function disconnect() {
  ws?.close()
  ws = undefined
  term?.dispose()
  term = undefined
  connected.value = false
}

watch(hostId, () => {
  if (connected.value || term) void connect()
})

onMounted(async () => {
  try {
    hosts.value = await listHosts()
    if (hosts.value.length) hostId.value = hosts.value[0].id
  } catch (e) {
    toast.error(e)
  }
})

onBeforeUnmount(disconnect)
</script>

<template>
  <div class="flex h-full flex-col gap-3">
    <div class="flex items-center gap-2">
      <select v-model.number="hostId" class="input w-48">
        <option v-for="h in hosts" :key="h.id" :value="h.id">{{ h.name }}</option>
      </select>
      <button class="btn btn-primary" :disabled="hostId == null" @click="connect()">
        {{ connected ? '重连' : '连接' }}
      </button>
      <button class="btn" :disabled="!connected" @click="disconnect()">断开</button>
      <span class="badge" :class="connected ? 'bg-ok/15 text-ok' : 'bg-raised text-muted'">
        {{ connected ? '已连接' : '未连接' }}
      </span>
    </div>

    <div class="flex-1 overflow-hidden rounded-lg border border-border bg-bg p-2">
      <div ref="container" class="h-full" />
      <p v-if="!term" class="grid h-full place-items-center text-sm text-muted">选择主机后点击连接</p>
    </div>
  </div>
</template>
