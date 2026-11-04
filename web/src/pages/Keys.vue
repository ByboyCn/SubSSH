<script setup lang="ts">
import { reactive, ref } from 'vue'
import { createKey, deleteKey, listKeys } from '@/api/client'
import type { SshKey } from '@/types'
import { useToastStore } from '@/stores/toast'
import Modal from '@/components/ui/Modal.vue'

const toast = useToastStore()
const keys = ref<SshKey[]>([])
const loading = ref(true)
const submitting = ref(false)
const showForm = ref(false)

async function refresh() {
  loading.value = true
  try {
    keys.value = await listKeys()
  } catch (e) {
    toast.error(e)
  } finally {
    loading.value = false
  }
}

const form = reactive<{ name: string; mode: 'generate' | 'import'; privateKey: string; passphrase: string }>({
  name: '',
  mode: 'generate',
  privateKey: '',
  passphrase: '',
})

function openCreate() {
  form.name = ''
  form.mode = 'generate'
  form.privateKey = ''
  form.passphrase = ''
  showForm.value = true
}

async function submit() {
  submitting.value = true
  try {
    await createKey({
      name: form.name,
      mode: form.mode,
      privateKey: form.mode === 'import' ? form.privateKey : undefined,
      passphrase: form.mode === 'import' && form.passphrase ? form.passphrase : undefined,
    })
    toast.success('密钥已创建')
    showForm.value = false
    form.name = ''
    form.privateKey = ''
    form.passphrase = ''
    await refresh()
  } catch (e) {
    toast.error(e)
  } finally {
    submitting.value = false
  }
}

async function remove(k: SshKey) {
  if (!window.confirm(`确定删除密钥「${k.name}」？正在使用它的主机会失去认证能力。`)) return
  try {
    await deleteKey(k.id)
    toast.success('密钥已删除')
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
      <span class="text-sm text-muted">密钥用于主机认证，私钥服务端加密存储</span>
      <button class="btn btn-primary" @click="openCreate">＋ 新建密钥</button>
    </div>

    <div class="card overflow-x-auto">
      <table class="table-base">
        <thead>
          <tr><th>名称</th><th>公钥指纹 / 内容</th><th>创建时间</th><th class="w-24">操作</th></tr>
        </thead>
        <tbody>
          <tr v-for="k in keys" :key="k.id">
            <td class="font-medium">{{ k.name }}</td>
            <td class="max-w-md truncate font-mono text-xs text-muted" :title="k.publicKey">{{ k.publicKey }}</td>
            <td class="text-muted text-xs">{{ fmtTs(k.createdAt) }}</td>
            <td><button class="btn btn-danger" @click="remove(k)">删除</button></td>
          </tr>
          <tr v-if="!loading && keys.length === 0">
            <td colspan="4" class="text-center text-muted">暂无密钥</td>
          </tr>
        </tbody>
      </table>
      <div v-if="loading" class="py-4 text-center text-sm text-muted">加载中…</div>
    </div>

    <Modal v-if="showForm" title="新建密钥" @close="showForm = false">
      <form class="space-y-3" @submit.prevent="submit">
        <div>
          <label class="label">名称</label>
          <input v-model="form.name" required class="input" placeholder="prod-key" />
        </div>
        <div>
          <label class="label">模式</label>
          <select v-model="form.mode" class="input">
            <option value="generate">服务端生成</option>
            <option value="import">导入私钥</option>
          </select>
        </div>
        <template v-if="form.mode === 'import'">
          <div>
            <label class="label">私钥（OpenSSH/PEM）</label>
            <textarea v-model="form.privateKey" required rows="6" class="input font-mono text-xs" />
          </div>
          <div>
            <label class="label">私钥口令（可选，服务端解密后丢弃）</label>
            <input v-model="form.passphrase" type="password" class="input" autocomplete="off" />
          </div>
        </template>
        <div class="flex justify-end gap-2">
          <button type="button" class="btn" @click="showForm = false">取消</button>
          <button type="submit" class="btn btn-primary" :disabled="submitting">
            {{ submitting ? '创建中…' : '创建' }}
          </button>
        </div>
      </form>
    </Modal>
  </div>
</template>
