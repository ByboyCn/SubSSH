<script setup lang="ts">
import { useRouter } from 'vue-router'

const router = useRouter()
</script>

<template>
  <div class="grid h-full place-items-center">
    <div class="text-center">
      <div class="text-4xl font-semibold text-muted">404</div>
      <p class="mt-2 text-sm text-muted">页面不存在</p>
      <button class="btn btn-primary mt-4" @click="router.replace('/')">返回概览</button>
    </div>
  </div>
</template>
