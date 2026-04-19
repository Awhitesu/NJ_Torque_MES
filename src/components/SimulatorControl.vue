<script setup lang="ts">
import { ref } from 'vue'
import { MOCK_ORDER_INFO, MOCK_ROUTE_DATA, MockHardware } from '../utils/mockData'
import type { TighteningTask } from '../types/mes'

const props = defineProps<{
  productCode: string
  tasks: TighteningTask[]
}>()

const emit = defineEmits<{
  (e: 'update:productCode', val: string): void
  (e: 'mockScan'): void
  (e: 'mockTorque', result: { torque: string, angle: string, status: string }): void
  (e: 'log', level: 'info'|'success'|'warn'|'error', msg: string): void
}>()

function simulateProductScan() {
  emit('update:productCode', 'MOCK-PRODUCT-12345')
  emit('log', 'info', '[模拟] 已填入模拟条码，请点击查询')
}

function simulateMaterialScan() {
  // 查找当前未完成的物料 (这里逻辑比较简化，直接模拟生成符合要求的条码)
  // 实际上用户可以手动输入，这里提供一键生成
  const code = MockHardware.generateBarcode('MAT-BASE-001', 12)
  emit('log', 'info', `[模拟] 生成条码: ${code} (已复制到剪贴板功能待实现，请手动填入物料验证框)`)
  // 我们可以通过 window 事件或者直接 emit 
  window.dispatchEvent(new CustomEvent('mock:barcode', { detail: code }))
}

function simulateTorque() {
  // 寻找下一个需要拧紧的任务
  const nextIdx = props.tasks.findIndex(t => !t.actualTorque)
  if (nextIdx === -1) {
    emit('log', 'warn', '[模拟] 当前无待完成的拧紧任务')
    return
  }

  const task = props.tasks[nextIdx]
  const result = MockHardware.simulateTorqueResult(1.5, task.torqueMin, task.torqueMax)
  
  emit('log', 'info', `[模拟] 产生定扭结果: ${result.torque}Nm, ${result.angle}Deg [${result.status}]`)
  emit('mockTorque', result)
}
</script>

<template>
  <div class="simulator-card">
    <div class="sim-header">
      <span class="sim-icon">🧪</span>
      <span class="sim-title">模拟交互控制台 (Simulation)</span>
    </div>
    
    <div class="sim-body">
      <div class="sim-section">
        <label>1. 条码扫描模拟</label>
        <div class="btn-group">
          <button class="sim-btn" @click="simulateProductScan">模拟扫描产品条码</button>
          <button class="sim-btn outline" @click="simulateMaterialScan">模拟扫描底板条码</button>
        </div>
      </div>

      <div class="sim-section">
        <label>2. 硬件数据模拟</label>
        <div class="btn-group">
          <button class="sim-btn primary" @click="simulateTorque">模拟产生一次拧紧结果</button>
        </div>
      </div>

      <div class="sim-hint">
        💡 提示：本控制台用于在无硬件连接时测试系统逻辑流转。
      </div>
    </div>
  </div>
</template>

<style scoped>
.simulator-card {
  background: rgba(255, 152, 0, 0.05);
  border: 1px dashed rgba(255, 152, 0, 0.3);
  border-radius: 8px;
  margin: 10px 0;
  overflow: hidden;
}

.sim-header {
  background: rgba(255, 152, 0, 0.1);
  padding: 8px 12px;
  display: flex;
  align-items: center;
  gap: 8px;
  border-bottom: 1px solid rgba(255, 152, 0, 0.1);
}

.sim-icon { font-size: 14px; }
.sim-title {
  font-size: 12px;
  font-weight: 700;
  color: #ffb74d;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.sim-body {
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.sim-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.sim-section label {
  font-size: 11px;
  color: #8d6e63;
  font-weight: 600;
}

.btn-group {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.sim-btn {
  background: #4e342e;
  border: 1px solid #5d4037;
  color: #d7ccc8;
  padding: 6px 12px;
  border-radius: 4px;
  font-size: 11px;
  cursor: pointer;
  transition: all 0.2s;
}

.sim-btn:hover {
  background: #5d4037;
  border-color: #ffb74d;
  color: #fff;
}

.sim-btn.primary {
  background: #ef6c00;
  border-color: #ff9800;
  color: #fff;
}
.sim-btn.primary:hover {
  background: #e65100;
}

.sim-btn.outline {
  background: transparent;
  border: 1px solid #795548;
}

.sim-hint {
  font-size: 10px;
  color: #6d4c41;
  font-style: italic;
  margin-top: 4px;
}
</style>
