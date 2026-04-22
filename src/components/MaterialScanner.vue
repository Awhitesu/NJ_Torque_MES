<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import type { RouteStep, WorkStep } from '../types/mes'

const props = defineProps<{
  steps: RouteStep[]
  productCode?: string
}>()

const emit = defineEmits<{
  (e: 'complete', materials: { productCode: string, productCount: number }[]): void
  (e: 'single-complete', material: { productCode: string, productCount: number }): void
  (e: 'log', level: 'info'|'success'|'warn'|'error', msg: string): void
}>()

export interface MaterialTask {
  uid: string
  seqIdx: number
  material_No: string
  material_Name: string
  material_number: number
  noLength: number
  retrospect_Type: unknown
  scannedCount: number
  scannedBarcodes: string[]
  status: 'pending' | 'completed'
}

const taskList = ref<MaterialTask[]>([])
const lastScannedCode = ref('')
const autoBoundMaterials = ref<{ productCode: string, productCount: number }[]>([])
const completionEmitted = ref(false)

function isBarcodeMatchMaterial(barcode: string, materialNo: string, noLength: number): boolean {
  if (!barcode || !materialNo) return false
  if (noLength > 0 && barcode.length !== noLength) return false
  return barcode.startsWith(materialNo)
}

function buildCompletePayload() {
  const scannedPayload = taskList.value.flatMap(t =>
    t.scannedBarcodes.map(code => ({
      productCode: code,
      productCount: 1
    }))
  )
  return [...scannedPayload, ...autoBoundMaterials.value]
}

function tryEmitComplete() {
  if (completionEmitted.value) return
  const visibleDone = taskList.value.length === 0 || taskList.value.every(t => t.status === 'completed')
  if (!visibleDone) return

  const payload = buildCompletePayload()
  if (!payload.length) return
  completionEmitted.value = true
  emit('complete', payload)
}

function buildTasks() {
  const tasks: MaterialTask[] = []
  let uidCounter = 0
  const autoMats: { productCode: string, productCount: number }[] = []
  const currentProductCode = String(props.productCode || '').trim()
  completionEmitted.value = false

  props.steps.forEach((seq, si) => {
    const wsList = (seq.workStepList as WorkStep[]) || []
    wsList.forEach((ws) => {
      const matList = (ws.workStepMaterialList as any[]) || []
      matList.forEach((mat) => {
        const reqNum = Number(mat.material_number) || 0
        if (!mat.material_No || reqNum <= 0) return
        const noLength = Number(mat.noLength) || 0

        // 与产品条码匹配的物料，不在列表显示，但需要参与后续上传。
        if (currentProductCode && isBarcodeMatchMaterial(currentProductCode, mat.material_No, noLength)) {
          for (let i = 0; i < reqNum; i++) {
            autoMats.push({ productCode: currentProductCode, productCount: 1 })
          }
          return
        }

        tasks.push({
          uid: `mat-${uidCounter++}`,
          seqIdx: si + 1,
          material_No: mat.material_No,
          material_Name: mat.material_Name || '',
          material_number: reqNum,
          noLength,
          retrospect_Type: mat.retrospect_Type,
          scannedCount: 0,
          scannedBarcodes: [],
          status: 'pending'
        })
      })
    })
  })

  taskList.value = tasks
  autoBoundMaterials.value = autoMats

  if (autoMats.length > 0) {
    autoMats.forEach(item => emit('single-complete', item))
  }
  tryEmitComplete()
}

watch(
  () => [props.steps, props.productCode],
  () => {
    buildTasks()
  },
  { immediate: true, deep: true }
)

const isAllCompleted = computed(() => {
  const visibleDone = taskList.value.length === 0 || taskList.value.every(t => t.status === 'completed')
  return visibleDone && buildCompletePayload().length > 0
})

function handleIncomingCode(rawCode: string) {
  const code = rawCode.trim()
  if (!code) return
  lastScannedCode.value = code

  const isCodeMatchTask = (t: MaterialTask) => {
    if (t.noLength > 0 && code.length !== t.noLength) return false
    if (!code.startsWith(t.material_No)) return false
    return true
  }

  // 重复扫码：
  // 1) 同一个条码已扫过；
  // 2) 该物料已达到需求数后再次扫码。
  const matchedTask = taskList.value.find(t => isCodeMatchTask(t))
  if (matchedTask) {
    const scannedSameCode = matchedTask.scannedBarcodes.includes(code)
    const reachedRequired = matchedTask.scannedCount >= matchedTask.material_number
    if (scannedSameCode || reachedRequired) {
      emit('log', 'error', `扫码重复: ${matchedTask.material_Name || matchedTask.material_No} 已达到需求数(${matchedTask.material_number})`)
      lastScannedCode.value = ''
      return
    }
  }

  const target = taskList.value.find(t => {
    if (t.status === 'completed') return false
    return isCodeMatchTask(t)
  })

  if (target) {
    target.scannedCount++
    target.scannedBarcodes.push(code)

    if (target.scannedCount >= target.material_number) {
      target.status = 'completed'
      emit('log', 'success', `物料扫描匹配成功: ${target.material_Name} (全部完成)`)
    } else {
      emit('log', 'success', `物料扫描匹配成功: ${target.material_Name} (${target.scannedCount}/${target.material_number})`)
    }

    emit('single-complete', { productCode: code, productCount: 1 })

    if (isAllCompleted.value) {
      emit('log', 'success', '🎉 所有物料验证已全部通过！')
      tryEmitComplete()
    }
  } else {
    emit('log', 'error', `扫码无匹配物料或该物料已扫完: ${code}`)
  }
}

defineExpose({
  consumeScannedCode: (code: string) => {
    handleIncomingCode(code)
  }
})
</script>

<template>
  <div class="material-scanner-panel">
    <div class="scan-action-bar">
      <div class="scan-input-wrapper">
        <span class="icon">🔫</span>
        <div class="scanner-only-text">
          扫码枪监听中（已禁用人工输入）
        </div>
      </div>

      <div v-if="taskList.length" class="progress-status">
        状态:
        <span v-if="isAllCompleted" class="status-all-done">✅ 全部验证通过</span>
        <span v-else class="status-pending">⏳ 等待验证 ({{ taskList.filter(t => t.status === 'completed').length }}/{{ taskList.length }})</span>
      </div>
    </div>

    <div v-if="!taskList.length" class="empty-state">
      当前工步无物料绑定信息，无需扫描验证。
    </div>

    <div v-else class="table-scroll">
      <table>
        <thead>
          <tr>
            <th style="width: 40px">序号</th>
            <th>物料编号</th>
            <th>物料名称</th>
            <th style="width: 60px" class="center">需求数</th>
            <th style="width: 60px" class="center">条码长度</th>
            <th style="width: 80px" class="center">追溯类型</th>
            <th style="width: 80px" class="center">已扫数量</th>
            <th style="width: 92px" class="center">状态</th>
            <th>已匹配条码</th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="(task, idx) in taskList"
            :key="task.uid"
            class="data-row"
            :class="{ 'done-row': task.status === 'completed' }"
          >
            <td>
              <span class="seq-badge" :class="{ 'done-badge': task.status === 'completed' }">{{ idx + 1 }}</span>
            </td>
            <td class="mono c-blue">{{ task.material_No }}</td>
            <td class="mat-name">{{ task.material_Name }}</td>
            <td class="center req-num">{{ task.material_number }}</td>
            <td class="center">{{ task.noLength > 0 ? task.noLength : '—' }}</td>
            <td class="center">{{ task.retrospect_Type ?? '—' }}</td>

            <td class="center">
              <span class="scan-count" :class="{ 'full': task.scannedCount >= task.material_number, 'partial': task.scannedCount > 0 && task.scannedCount < task.material_number }">
                {{ task.scannedCount }}
              </span>
            </td>

            <td class="center">
              <span v-if="task.status === 'completed'" class="status-tag success">通过</span>
              <span v-else class="status-tag pending">待扫</span>
            </td>

            <td class="barcodes-cell mono small">
              <div v-for="(code, i) in task.scannedBarcodes" :key="i" class="code-item">
                {{ code }}
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.material-scanner-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.scan-action-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 18px;
  background: rgba(13, 71, 161, 0.15);
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  gap: 20px;
  flex-shrink: 0;
}

.scan-input-wrapper {
  flex: 1;
  max-width: 760px;
  display: flex;
  align-items: center;
  background: #0d1117;
  border: 1px solid rgba(100, 181, 246, 0.3);
  border-radius: 10px;
  padding: 14px 16px;
}

.scan-input-wrapper .icon {
  margin: 0 10px;
  font-size: 22px;
  opacity: 0.6;
}

.scanner-only-text {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 6px;
  color: #e3f2fd;
  font-size: 18px;
  font-weight: 600;
}

.progress-status {
  font-size: 20px;
  font-weight: 600;
  white-space: nowrap;
}

.status-all-done { color: #00e676; animation: pulse 2s infinite; }
.status-pending { color: #ffab40; }

.empty-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #546e7a;
  font-size: 16px;
}

.table-scroll {
  flex: 1;
  overflow: auto;
}
.table-scroll::-webkit-scrollbar { width: 4px; height: 4px; }
.table-scroll::-webkit-scrollbar-thumb { background: rgba(100, 181, 246, 0.2); }

table {
  width: 100%;
  border-collapse: collapse;
  font-size: 15px;
}
thead tr {
  background: rgba(21, 101, 192, 0.2);
  position: sticky;
  top: 0;
  z-index: 2;
}
th {
  padding: 12px 14px;
  text-align: left;
  color: #78909c;
  font-weight: 600;
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  white-space: nowrap;
  font-size: 15px;
}

.data-row {
  border-bottom: 1px solid rgba(100, 181, 246, 0.05);
  transition: background 0.15s;
}
.data-row:hover { background: rgba(66, 165, 245, 0.04); }

.done-row { background: rgba(0, 230, 118, 0.03); }

td {
  padding: 12px 14px;
  color: #cfd8dc;
  vertical-align: middle;
  font-size: 15px;
}
.center { text-align: center; }
.mono { font-family: 'Consolas', monospace; }
.small { font-size: 13px; }
.c-blue { color: #64b5f6; }
.mat-name { font-weight: 600; color: #e0e6ed; font-size: 16px; }
.req-num { font-weight: 700; color: #90caf9; font-size: 16px; }

.seq-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  background: rgba(100, 181, 246, 0.15);
  border-radius: 8px;
  font-size: 14px;
  font-weight: 700;
  color: #90caf9;
}
.done-badge { background: #00e676; color: #000; }

.scan-count {
  font-size: 18px;
  font-weight: 700;
  color: #78909c;
}
.scan-count.partial { color: #ffab40; }
.scan-count.full { color: #00e676; }

.status-tag {
  padding: 5px 12px;
  border-radius: 12px;
  font-size: 14px;
  font-weight: 600;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  white-space: nowrap;
  line-height: 1;
}
.status-tag.pending { background: rgba(255, 171, 64, 0.15); color: #ffab40; }
.status-tag.success { background: rgba(0, 230, 118, 0.15); color: #00e676; }

.barcodes-cell {
  line-height: 1.7;
}

.code-item {
  color: #80cbc4;
  background: rgba(128, 203, 196, 0.1);
  padding: 3px 9px;
  border-radius: 4px;
  margin-bottom: 4px;
  display: inline-block;
  font-size: 13px;
}
.code-item:last-child { margin-bottom: 0; }

@keyframes pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.6; }
}
</style>
