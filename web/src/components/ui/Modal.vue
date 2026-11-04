<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'

const props = defineProps<{ title: string; width?: string }>()
const emit = defineEmits<{ close: [] }>()

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}
onMounted(() => document.addEventListener('keydown', onKey))
onUnmounted(() => document.removeEventListener('keydown', onKey))
</script>

<template>
  <Teleport to="body">
    <div
      class="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/50 p-6 pt-[8vh]"
      @click.self="emit('close')"
    >
      <div class="card w-full shadow-xl" :class="width || 'max-w-xl'">
        <div class="mb-4 flex items-center justify-between">
          <h2 class="text-sm font-medium">{{ props.title }}</h2>
          <button class="btn px-2" title="关闭" @click="emit('close')">✕</button>
        </div>
        <slot />
      </div>
    </div>
  </Teleport>
</template>
