/**
 * 导航定义：分组与图标对齐原版 components/layout/nav-items.ts。
 * 图标用内联 SVG（简单几何形）代替 @phosphor-icons/react，避免额外依赖。
 */
import type { Component } from 'vue'
import { h } from 'vue'

export type NavItem = { to: string; label: string; icon: Component }
export type NavGroup = { title?: string; items: NavItem[] }

function icon(path: string): Component {
  return () =>
    h('svg', { viewBox: '0 0 16 16', fill: 'none', stroke: 'currentColor', class: 'size-4 shrink-0' }, [
      h('path', { d: path, 'stroke-width': '1.5', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }),
    ])
}

export const navGroups: NavGroup[] = [
  { items: [{ to: '/', label: '概览', icon: icon('M8 1.5 14.5 5v6L8 14.5 1.5 11V5zM1.5 5 8 8.5 14.5 5M8 8.5v6') }] },
  {
    title: '资源',
    items: [
      { to: '/hosts', label: '主机', icon: icon('M2 3h12v5H2zM2 8v5h12V8M4.5 5.5h.01M4.5 10.5h.01') },
      { to: '/keys', label: '密钥', icon: icon('M10.5 2a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7zM7.5 6.5 2 12v2h2l1-1h1.5v-1.5H8V10h1.5') },
      { to: '/tokens', label: '令牌', icon: icon('M1.5 4.5h13v7h-13zM1.5 8h13M11 11.5h1') },
    ],
  },
  {
    title: '操作',
    items: [
      { to: '/terminal', label: '终端', icon: icon('M2 2.5h12v11H2zM4 6l2 2-2 2M8 10h4') },
      { to: '/files', label: '文件', icon: icon('M1.5 4.5h5l1.5 2h7v7h-13zM1.5 4.5v-2h4l1.5 2') },
      { to: '/jobs', label: '任务', icon: icon('M8 1.5v3M8 11.5v3M1.5 8h3M11.5 8h3M8 5.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5') },
    ],
  },
  {
    title: '观测',
    items: [
      { to: '/activity', label: '活动', icon: icon('M1.5 8h3l1.5-4 2.5 8 1.5-4h4.5') },
      { to: '/metrics', label: '指标', icon: icon('M1.5 2v12h13M4.5 11l3-3.5 2.5 2 3.5-5') },
      { to: '/memories', label: '记忆', icon: icon('M8 1.5a4.5 4.5 0 0 0-3 7.9V13h6v-3.6a4.5 4.5 0 0 0-3-7.9zM6.5 13v1.5h3V13') },
    ],
  },
]

const flat = navGroups.flatMap((g) => g.items)

/** 顶栏标题取自同一份导航定义，避免路由与标题两处维护 */
export function titleForPath(pathname: string): string {
  const item = flat.find((i) => (i.to === '/' ? pathname === '/' : pathname.startsWith(i.to)))
  return item?.label ?? '未找到'
}
