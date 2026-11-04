<script setup lang="ts">
import { RouterLink, useRoute } from 'vue-router'
import { navGroups } from './nav-items'

const route = useRoute()
</script>

<template>
  <aside class="flex w-56 shrink-0 flex-col border-r border-border bg-surface">
    <div class="flex items-center gap-2 border-b border-border px-4 py-4">
      <div class="grid size-8 place-items-center rounded-md bg-accent/15 font-bold text-accent">S</div>
      <div>
        <div class="text-sm font-semibold">SubSsh</div>
        <div class="text-xs text-muted">SSH 网关控制台</div>
      </div>
    </div>

    <nav class="flex-1 overflow-y-auto px-2 py-3">
      <template v-for="(group, gi) in navGroups" :key="gi">
        <div v-if="group.title" class="px-2 pb-1 pt-4 text-xs text-muted">{{ group.title }}</div>
        <RouterLink
          v-for="item in group.items"
          :key="item.to"
          :to="item.to"
          class="mb-0.5 flex items-center gap-2.5 rounded-md px-2 py-1.5 text-sm transition-colors"
          :class="
            route.path === item.to || (item.to !== '/' && route.path.startsWith(item.to))
              ? 'bg-accent/15 text-accent'
              : 'text-muted hover:bg-raised hover:text-text'
          "
        >
          <component :is="item.icon" />
          {{ item.label }}
        </RouterLink>
      </template>
    </nav>
  </aside>
</template>
