<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import {
  deleteHost,
  listHosts,
  listKeys,
  resetFingerprint,
  saveHost,
  testHost,
} from '@/api/client'
import type { Host, HostPayload } from '@/types'
import { useToastStore } from '@/stores/toast'
import Modal from '@/components/ui/Modal.vue'

const toast = useToastStore()
const hosts = ref<Host[]>([])
const keys = ref<{ id: number; name: string }[]>([])
const loading = ref(true)
const submitting = ref(false)
const showForm = ref(false)
const editingId = ref<number | undefined>()
const busyHost = ref<number | null>(null)
const testOutput = ref<{ host: string; output: string } | null>(null)

async function refresh() {
  loading.value = true
  try {
    const [h, k] = await Promise.all([listHosts(), listKeys()])
    hosts.value = h
    keys.value = k.map(({ id, name }) => ({ id, name }))
  } catch (e) {
    toast.error(e)
  } finally {
    loading.value = false
  }
}

const emptyForm = (): HostPayload & { id?: number } => ({
  name: '',
  username: 'root',
  addr: '',
  port: 22,
  authType: 'key',
  keyId: undefined,
  password: '',
  jumpHost: '',
  monitorEnabled: true,
  tags: [],
})

const form = reactive(emptyForm())

const showArchived = ref(false)
const filtered = computed(() =>
  showArchived.value ? hosts.value : hosts.value.filter((h) => !h.archived),
)

function openCreate() {
  editingId.value = undefined
  Object.assign(form, emptyForm())
  showForm.value = true
}

function openEdit(h: Host) {
  editingId.value = h.id
  Object.assign(form, {
    id: h.id,
    name: h.name,
    username: h.username,
    addr: h.addr,
    port: h.port,
    authType: h.authType,
    keyId: h.keyId,
    password: '',
    jumpHost: '',
    monitorEnabled: h.monitorEnabled,
    tags: [...h.tags],
  })
  showForm.value = true
}

async function submit() {
  submitting.value = true
  try {
    const payload: HostPayload = {
      name: form.name,
      username: form.username,
      addr: form.addr,
      port: form.port,
      authType: form.authType,
      keyId: form.authType === 'key' ? form.keyId : undefined,
      password: form.authType === 'password' ? form.password : undefined,
      jumpHost: form.jumpHost || undefined,
      monitorEnabled: form.monitorEnabled,
      tags: (form.tags ?? []).filter(Boolean),
    }
    await saveHost(editingId.value, payload)
    toast.success(editingId.value ? '主机已保存' : '主机已创建')
    showForm.value = false
    await refresh()
  } catch (e) {
    toast.error(e)
  } finally {
    submitting.value = false
  }
}

async function remove(h: Host) {
  if (!window.confirm(`确定删除主机「${h.name}」？此操作不可撤销。`)) return
  try {
    await deleteHost(h.id)
    toast.success('主机已删除')
    await refresh()
  } catch (e) {
    toast.error(e)
  }
}

async function test(h: Host) {
  busyHost.value = h.id
  testOutput.value = null
  try {
    const { output } = await testHost(h.id)
    testOutput.value = { host: h.name, output: output ?? '(无输出，连接成功)' }
    toast.success(`「${h.name}」连接成功`)
  } catch (e) {
    toast.error(e)
  } finally {
    busyHost.value = null
  }
}

async function resetFp(h: Host) {
  if (!window.confirm(`重置「${h.name}」已固定的主机指纹？下次连接将重新执行 TOFU 确认。`)) return
  try {
    await resetFingerprint(h.id)
    toast.success('指纹已重置')
    await refresh()
  } catch (e) {
    toast.error(e)
  }
}

const fmtTs = (ts: number) => new Date(ts * 1000).toLocaleString()
</script>

<template>
  <div class="space-y-4">
    <div class="flex items-center justify-between">
      <label class="flex items-center gap-2 text-sm text-muted">
        <input v-model="showArchived" type="checkbox" class="accent-accent" />
        显示已归档主机
      </label>
      <button class="btn btn-primary" @click="openCreate">＋ 新建主机</button>
    </div>

    <div class="card overflow-x-auto">
      <table class="table-base">
        <thead>
          <tr>
            <th>名称</th><th>地址</th><th>认证</th><th>跳板</th><th>标签</th><th>监控</th><th>状态</th><th>创建时间</th><th class="w-64">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="h in filtered" :key="h.id">
            <td class="font-medium">
              {{ h.name }}
              <span v-if="h.archived" class="badge ml-1 bg-warn/15 text-warn">已归档</span>
            </td>
            <td class="font-mono text-xs">{{ h.username }}@{{ h.addr }}:{{ h.port }}</td>
            <td>{{ h.authType === 'key' ? `密钥 #${h.keyId ?? '?'}` : '密码' }}</td>
            <td>{{ h.jumpHostId != null ? `#${h.jumpHostId}` : '直连' }}</td>
            <td>
              <span v-for="t in h.tags" :key="t" class="badge mr-1 bg-raised text-muted">{{ t }}</span>
            </td>
            <td>{{ h.monitorEnabled ? '✓' : '—' }}</td>
            <td>
              <span class="badge" :class="h.hostKeyFp ? 'bg-ok/15 text-ok' : 'bg-warn/15 text-warn'">
                {{ h.hostKeyFp ? '指纹已固定' : '待 TOFU' }}
              </span>
            </td>
            <td class="text-muted text-xs">{{ fmtTs(h.createdAt) }}</td>
            <td>
              <div class="flex gap-1.5">
                <button class="btn" :disabled="busyHost === h.id" @click="test(h)">测试</button>
                <button class="btn" @click="openEdit(h)">编辑</button>
                <button class="btn" @click="resetFp(h)">重置指纹</button>
                <button class="btn btn-danger" @click="remove(h)">删除</button>
              </div>
            </td>
          </tr>
          <tr v-if="!loading && filtered.length === 0">
            <td colspan="9" class="text-center text-muted">暂无主机</td>
          </tr>
        </tbody>
      </table>
      <div v-if="loading" class="py-4 text-center text-sm text-muted">加载中…</div>
    </div>

    <pre v-if="testOutput" class="card font-mono text-xs text-muted">[{{ testOutput.host }}] 测试输出：
{{ testOutput.output }}</pre>

    <!-- 创建 / 编辑表单（弹窗） -->
    <Modal v-if="showForm" :title="editingId ? '编辑主机' : '新建主机'" width="max-w-2xl" @close="showForm = false">
      <form class="grid grid-cols-2 gap-4" @submit.prevent="submit">
        <div>
          <label class="label">名称</label>
          <input v-model="form.name" required class="input" placeholder="web-01" />
        </div>
        <div>
          <label class="label">跳板主机（可选）</label>
          <input v-model="form.jumpHost" class="input" placeholder="bastion" />
        </div>
        <div>
          <label class="label">地址</label>
          <input v-model="form.addr" required class="input" placeholder="10.0.0.1" />
        </div>
        <div>
          <label class="label">端口</label>
          <input v-model.number="form.port" type="number" min="1" max="65535" required class="input" />
        </div>
        <div>
          <label class="label">用户名</label>
          <input v-model="form.username" required class="input" />
        </div>
        <div>
          <label class="label">认证方式</label>
          <select v-model="form.authType" class="input">
            <option value="key">密钥</option>
            <option value="password">密码</option>
          </select>
        </div>
        <div v-if="form.authType === 'key'">
          <label class="label">SSH 密钥</label>
          <select v-model.number="form.keyId" required class="input">
            <option :value="undefined" disabled>选择密钥</option>
            <option v-for="k in keys" :key="k.id" :value="k.id">#{{ k.id }} {{ k.name }}</option>
          </select>
        </div>
        <div v-else>
          <label class="label">密码（仅创建时提交，不回显）</label>
          <input v-model="form.password" type="password" class="input" autocomplete="new-password" />
        </div>
        <div class="col-span-2">
          <label class="label">标签（逗号分隔）</label>
          <input
            class="input"
            :value="(form.tags ?? []).join(',')"
            placeholder="prod,web"
            @input="form.tags = ($event.target as HTMLInputElement).value.split(',').map((s) => s.trim())"
          />
        </div>
        <label class="col-span-2 flex items-center gap-2 text-sm">
          <input v-model="form.monitorEnabled" type="checkbox" class="accent-accent" />
          启用资源监控
        </label>
        <div class="col-span-2 flex justify-end gap-2">
          <button type="button" class="btn" @click="showForm = false">取消</button>
          <button type="submit" class="btn btn-primary" :disabled="submitting">
            {{ submitting ? '保存中…' : '保存' }}
          </button>
        </div>
      </form>
    </Modal>
  </div>
</template>
