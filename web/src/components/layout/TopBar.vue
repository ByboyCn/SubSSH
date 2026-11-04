<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { titleForPath } from './nav-items'
import { useAuthStore } from '@/stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const title = computed(() => titleForPath(route.path))

/** 主题切换占位：持久化偏好与样式变量待接入 */
const theme = ref<'dark' | 'light'>('dark')
function toggleTheme() {
  theme.value = theme.value === 'dark' ? 'light' : 'dark'
  // TODO: 接入 document.documentElement.classList 与 localStorage
}

function logout() {
  auth.clear()
  void router.push({ name: 'login' })
}
</script>

<template>
  <header class="flex h-14 shrink-0 items-center justify-between border-b border-border bg-surface px-6">
    <h1 class="text-base font-semibold">{{ title }}</h1>
    <div class="flex items-center gap-2">
      <button class="btn" :title="`切换主题（当前 ${theme}，占位）`" @click="toggleTheme">
        {{ theme === 'dark' ? '🌙' : '☀️' }}
      </button>
      <button class="btn" @click="logout">登出</button>
    </div>
  </header>
</template>
