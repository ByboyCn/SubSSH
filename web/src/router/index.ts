import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '@/stores/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'dashboard', component: () => import('@/pages/Dashboard.vue') },
    { path: '/hosts', name: 'hosts', component: () => import('@/pages/Hosts.vue') },
    { path: '/keys', name: 'keys', component: () => import('@/pages/Keys.vue') },
    { path: '/tokens', name: 'tokens', component: () => import('@/pages/Tokens.vue') },
    { path: '/terminal', name: 'terminal', component: () => import('@/pages/Terminal.vue') },
    { path: '/files', name: 'files', component: () => import('@/pages/Files.vue') },
    { path: '/jobs', name: 'jobs', component: () => import('@/pages/Jobs.vue') },
    { path: '/activity', name: 'activity', component: () => import('@/pages/Activity.vue') },
    { path: '/metrics', name: 'metrics', component: () => import('@/pages/Metrics.vue') },
    { path: '/memories', name: 'memories', component: () => import('@/pages/Memories.vue') },
    {
      path: '/login',
      name: 'login',
      component: () => import('@/pages/Login.vue'),
      meta: { public: true },
    },
    {
      path: '/oauth/authorization',
      name: 'oauth-authorize',
      component: () => import('@/pages/OAuthAuthorize.vue'),
      meta: { public: true },
    },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('@/pages/NotFound.vue') },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  if (to.meta.public) return true
  if (auth.authed) return true
  // 首次进入受保护路由：探测一次受保护接口确认会话仍然有效
  if (auth.checking) {
    try {
      const { listHosts } = await import('@/api/client')
      await listHosts()
      auth.setAuthed()
      return true
    } catch {
      auth.clear()
    }
  }
  return { name: 'login', query: { next: to.fullPath } }
})

export default router
