import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import router from './router'
import { useAuthStore } from './stores/auth'
import './style.css'

// 主题：挂载前恢复持久化偏好，避免闪屏
if (localStorage.getItem('subssh-theme') === 'light') {
  document.documentElement.classList.add('light')
}

const app = createApp(App)
app.use(createPinia())

const auth = useAuthStore()
auth.installUnauthorizedListener()

app.use(router)
app.mount('#app')
