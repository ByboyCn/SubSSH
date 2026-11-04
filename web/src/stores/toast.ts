/** 极简 toast 队列：供各页面统一提示成功/失败，无外部依赖 */
import { defineStore } from 'pinia'

export type ToastKind = 'success' | 'error' | 'info' | 'warning'

export interface ToastItem {
  id: number
  kind: ToastKind
  message: string
}

let nextId = 1

export const useToastStore = defineStore('toast', {
  state: () => ({ items: [] as ToastItem[] }),
  actions: {
    push(kind: ToastKind, message: string, ttl = 4000) {
      const id = nextId++
      this.items.push({ id, kind, message })
      window.setTimeout(() => this.remove(id), ttl)
    },
    success(message: string) {
      this.push('success', message)
    },
    error(e: unknown) {
      this.push('error', e instanceof Error ? e.message : String(e))
    },
    info(message: string) {
      this.push('info', message)
    },
    warning(message: string) {
      this.push('warning', message)
    },
    remove(id: number) {
      this.items = this.items.filter((t) => t.id !== id)
    },
  },
})
