/**
 * 会话状态：登录接口只暴露 setAuthed/clear；401 由 api 层广播事件驱动登出。
 * 是否已认证仅在成功访问受保护接口或登录成功后置位。
 */
import { defineStore } from 'pinia'
import { UNAUTHORIZED_EVENT } from '@/api/client'

export const useAuthStore = defineStore('auth', {
  state: () => ({
    authed: false,
    checking: true,
  }),
  actions: {
    setAuthed() {
      this.authed = true
      this.checking = false
    },
    clear() {
      this.authed = false
      this.checking = false
    },
    installUnauthorizedListener() {
      window.addEventListener(UNAUTHORIZED_EVENT, () => {
        this.clear()
        // 交给路由守卫把用户带到 /login 并记录回跳地址
        if (!location.pathname.startsWith('/login')) {
          location.assign('/login?next=' + encodeURIComponent(location.pathname + location.search))
        }
      })
    },
  },
})
