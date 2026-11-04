<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { oauthAuthorization, oauthDecision } from '@/api/client'
import type { Host, OAuthAuthorizationInfo, ToolGroup } from '@/types'
import { useToastStore } from '@/stores/toast'
import { useRoute, useRouter } from 'vue-router'

const route = useRoute()
const router = useRouter()
const toast = useToastStore()

const info = ref<OAuthAuthorizationInfo | null>(null)
const loading = ref(true)
const submitting = ref(false)
const error = ref('')
const allHosts = ref(true)
const manageHosts = ref(false)
const hostIds = ref<number[]>([])
const disabledTools = ref<string[]>([])

const infoHosts = ref<Host[]>([])
const infoGroups = ref<ToolGroup[]>([])

onMounted(async () => {
  try {
    // query 必须原样透传，后端要用它组装回调
    info.value = await oauthAuthorization(window.location.search)
    infoHosts.value = info.value.hosts
    infoGroups.value = info.value.toolGroups
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
})

async function decide(decision: 'approve' | 'deny') {
  submitting.value = true
  try {
    const result = await oauthDecision({
      query: window.location.search,
      decision,
      allHosts: allHosts.value,
      manageHosts: manageHosts.value,
      hostIds: allHosts.value ? undefined : hostIds.value,
      disabledTools: disabledTools.value,
    })
    window.location.assign(result.redirectUri)
  } catch (e) {
    toast.error(e)
  } finally {
    submitting.value = false
  }
}

function toggleHost(id: number) {
  hostIds.value = hostIds.value.includes(id) ? hostIds.value.filter((x) => x !== id) : [...hostIds.value, id]
}

function toggleTool(tool: string) {
  disabledTools.value = disabledTools.value.includes(tool)
    ? disabledTools.value.filter((x) => x !== tool)
    : [...disabledTools.value, tool]
}

function backToLogin() {
  // 未登录进入授权页时引导先登录，保留回跳
  void router.replace({ name: 'login', query: { next: route.fullPath } })
}
</script>

<template>
  <div class="grid h-full place-items-center p-6">
    <div v-if="error" class="card w-[480px] text-center">
      <p class="mb-3 text-sm text-danger">{{ error }}</p>
      <button class="btn" @click="backToLogin">返回登录</button>
    </div>

    <div v-else-if="loading" class="text-sm text-muted">加载授权信息…</div>

    <div v-else-if="info" class="card w-[560px]">
      <div class="mb-4">
        <h1 class="text-lg font-semibold">授权请求</h1>
        <p class="text-sm text-muted">
          应用 <span class="font-medium text-text">{{ info.clientName }}</span>
          <span v-if="info.clientUri">（<a :href="info.clientUri" target="_blank" rel="noopener" class="text-accent">{{ info.clientUri }}</a>）</span>
          请求访问你的 SSH 主机。
        </p>
      </div>

      <div class="mb-4">
        <div class="label">请求的权限范围</div>
        <div class="flex flex-wrap gap-1">
          <span v-for="s in info.requestedScopes" :key="s" class="badge bg-accent/15 font-mono text-accent">{{ s }}</span>
        </div>
      </div>

      <label class="mb-3 flex items-center gap-2 text-sm">
        <input v-model="allHosts" type="checkbox" class="accent-accent" /> 授予全部主机访问
      </label>
      <div v-if="!allHosts" class="mb-3 rounded-md border border-border p-2">
        <label v-for="h in infoHosts" :key="h.id" class="flex items-center gap-2 py-0.5 text-sm">
          <input type="checkbox" class="accent-accent" :checked="hostIds.includes(h.id)" @change="toggleHost(h.id)" />
          {{ h.name }} <span class="font-mono text-xs text-muted">{{ h.addr }}</span>
        </label>
      </div>

      <label class="mb-3 flex items-center gap-2 text-sm">
        <input v-model="manageHosts" type="checkbox" class="accent-accent" /> 允许管理主机
      </label>

      <div class="mb-4">
        <div class="label">禁用的工具（MCP 侧不可调用）</div>
        <div class="flex flex-wrap gap-1">
          <button
            v-for="g in infoGroups"
            :key="g.name"
            class="badge font-mono"
            :class="g.tools.some((t) => disabledTools.includes(t)) ? 'bg-danger/15 text-danger' : 'bg-raised text-muted'"
            @click="g.tools.forEach(toggleTool)"
          >
            {{ g.name }}
          </button>
          <span v-if="infoGroups.length === 0" class="text-xs text-muted">无工具组</span>
        </div>
      </div>

      <div class="mb-1 text-xs text-muted">回调地址</div>
      <code class="mb-4 block break-all rounded bg-bg px-2 py-1.5 font-mono text-xs">{{ info.redirectUri }}</code>

      <div class="flex justify-end gap-2">
        <button class="btn btn-danger" :disabled="submitting" @click="decide('deny')">拒绝</button>
        <button class="btn btn-primary" :disabled="submitting" @click="decide('approve')">批准</button>
      </div>
    </div>
  </div>
</template>
