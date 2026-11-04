<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api, post, UNAUTHORIZED_EVENT } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { useRoute, useRouter } from 'vue-router'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const password = ref('')
const submitting = ref(false)
const error = ref('')

async function submit() {
  submitting.value = true
  error.value = ''
  try {
    // 与原版一致：探测受保护接口验证管理密码会话
    await post('/auth/login', { password: password.value })
    auth.setAuthed()
    const next = typeof route.query.next === 'string' ? route.query.next : '/'
    void router.replace(next)
  } catch (e) {
    // 兼容未实现独立登录端点的后端：直接用会话探测判定
    try {
      await api('/hosts')
      auth.setAuthed()
      const next = typeof route.query.next === 'string' ? route.query.next : '/'
      void router.replace(next)
    } catch (probe) {
      error.value = probe instanceof Error ? probe.message : '登录失败'
    }
  } finally {
    submitting.value = false
  }
}

onMounted(() => {
  // 已登录则探测接口后直接跳转
  void api('/hosts')
    .then(() => {
      auth.setAuthed()
      const next = typeof route.query.next === 'string' ? route.query.next : '/'
      void router.replace(next)
    })
    .catch(() => undefined)
  window.addEventListener(UNAUTHORIZED_EVENT, () => (error.value = '会话已过期，请重新登录'))
})
</script>

<template>
  <div class="grid h-full place-items-center">
    <div class="card w-96">
      <div class="mb-6 text-center">
        <div class="mx-auto mb-2 grid size-10 place-items-center rounded-md bg-accent/15 text-lg font-bold text-accent">S</div>
        <h1 class="text-lg font-semibold">SubSsh 控制台</h1>
        <p class="text-sm text-muted">输入管理密码登录</p>
      </div>
      <form class="space-y-3" @submit.prevent="submit">
        <div>
          <label class="label">管理密码</label>
          <input v-model="password" type="password" required class="input" autocomplete="current-password" autofocus />
        </div>
        <p v-if="error" class="text-sm text-danger">{{ error }}</p>
        <button type="submit" class="btn btn-primary w-full justify-center" :disabled="submitting">
          {{ submitting ? '登录中…' : '登录' }}
        </button>
      </form>
    </div>
  </div>
</template>
