<script setup lang="ts">
import { useRoute } from 'vue-router'
import { computed } from 'vue'
import AppLayout from '@/components/layout/AppLayout.vue'
import { useToastStore } from '@/stores/toast'

const route = useRoute()
const toast = useToastStore()

const standalone = computed(() => route.meta.public === true)
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="fixed right-4 top-4 z-50 flex w-80 flex-col gap-2">
      <div
        v-for="t in toast.items"
        :key="t.id"
        class="rounded-md border px-3 py-2 text-sm shadow-lg"
        :class="{
          'border-ok/40 bg-surface text-ok': t.kind === 'success',
          'border-danger/40 bg-surface text-danger': t.kind === 'error',
          'border-warn/40 bg-surface text-warn': t.kind === 'warning',
          'border-border bg-surface text-text': t.kind === 'info',
        }"
        @click="toast.remove(t.id)"
      >
        {{ t.message }}
      </div>
    </div>

    <AppLayout v-if="!standalone">
      <router-view />
    </AppLayout>
    <router-view v-else />
  </div>
</template>
