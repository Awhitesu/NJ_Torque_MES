<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import type { AppConfig } from '../types/mes'
import { pickConfigDirectory } from '../services/appConfigApi'

const props = defineProps<{
  modelValue: AppConfig
  visible: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', val: AppConfig): void
  (e: 'update:visible', val: boolean): void
  (e: 'save'): void
}>()

const form = reactive<AppConfig>({ ...props.modelValue })
const pickingLogPath = ref(false)

watch(
  () => props.modelValue,
  (val) => Object.assign(form, val),
  { deep: true }
)

watch(
  () => props.visible,
  (v) => {
    if (v) Object.assign(form, props.modelValue)
  }
)

function handleSave() {
  const retryRaw = Number(form.tighteningMaxRetries)
  form.tighteningMaxRetries = Number.isFinite(retryRaw)
    ? Math.min(20, Math.max(1, Math.floor(retryRaw)))
    : 3

  emit('update:modelValue', { ...form })
  emit('save')
  emit('update:visible', false)
}

function handleCancel() {
  Object.assign(form, props.modelValue)
  emit('update:visible', false)
}

async function handlePickLogPath() {
  pickingLogPath.value = true
  try {
    const res = await pickConfigDirectory('请选择日志保存目录')
    if (res?.path) form.logSavePath = res.path
  } finally {
    pickingLogPath.value = false
  }
}
</script>

<template>
  <Transition name="modal">
    <div v-if="visible" class="modal-overlay" @click.self="handleCancel">
      <div class="modal-panel">
        <div class="modal-header">
          <span class="icon">⚙️</span>
          <h2>系统配置</h2>
          <button class="close-btn" @click="handleCancel">✕</button>
        </div>

        <div class="modal-body">
          <div class="section-title">系统参数设置</div>
          <div class="field-group">
            <label>本地日志保存路径 (Server Side)</label>
            <div class="dir-row">
              <input
                v-model="form.logSavePath"
                type="text"
                placeholder="C:\\MES_Logs"
                class="input-field"
              />
              <button class="btn-pick" type="button" :disabled="pickingLogPath" @click="handlePickLogPath">
                {{ pickingLogPath ? '选择中...' : '选择目录' }}
              </button>
            </div>
            <small>目录选择窗口从最顶层开始显示，优先便于快速选择目标目录。</small>
          </div>

          <div class="field-group">
            <label>定扭失败最大重试次数</label>
            <input
              v-model.number="form.tighteningMaxRetries"
              type="number"
              min="1"
              max="20"
              placeholder="3"
              class="input-field"
            />
            <small>达到该次数后流程停止，等待人工复位。</small>
          </div>

          <div class="field-groups-row" style="display: flex; gap: 16px; margin-top: 10px;">
            <div class="field-group" style="flex: 1;">
              <label>管理员账户</label>
              <input
                v-model="form.adminUsername"
                type="text"
                class="input-field"
              />
            </div>
            <div class="field-group" style="flex: 1;">
              <label>管理员密码</label>
              <input
                v-model="form.adminPassword"
                type="password"
                class="input-field"
              />
            </div>
          </div>

          <div class="section-title">MES 参数设置</div>
          <div class="field-group">
            <label>获取工单 API 地址</label>
            <input
              v-model="form.orderApiUrl"
              type="text"
              placeholder="/mes-api/api/OrderInfo/GetOtherOrderInfoByProcess"
              class="input-field"
            />
          </div>

          <div class="field-group">
            <label>获取工步 API 地址</label>
            <input
              v-model="form.routeApiUrl"
              type="text"
              placeholder="/mes-api/api/OrderInfo/GetTechRouteListByCode"
              class="input-field"
            />
          </div>

          <div class="field-group">
            <label>单物料验证 URL</label>
            <input
              v-model="form.singleMaterialApiUrl"
              type="text"
              placeholder="/mes-api/api/ProduceMessage/SingleCheckInput"
              class="input-field"
            />
          </div>

          <div class="field-group">
            <label>全物料验证 URL</label>
            <input
              v-model="form.fullMaterialApiUrl"
              type="text"
              placeholder="/mes-api/api/ProduceMessage/CompleteCheckInput"
              class="input-field"
            />
          </div>

          <div class="field-group">
            <label>MES 数据上传 URL</label>
            <input
              v-model="form.mesUploadApiUrl"
              type="text"
              placeholder="/mes-push/api/ProduceMessage/PushPackMessageToMes"
              class="input-field"
            />
          </div>

          <div class="field-group">
            <label>工序代码 (technicsProcessCode)</label>
            <input
              v-model="form.technicsProcessCode"
              type="text"
              class="input-field"
            />
          </div>

          <div class="field-group">
            <label>工序名称 (technicsProcessName)</label>
            <input
              v-model="form.technicsProcessName"
              type="text"
              class="input-field"
            />
          </div>

          <div class="field-groups-row" style="display: flex; gap: 16px;">
            <div class="field-group" style="flex: 1;">
              <label>userName</label>
              <input
                v-model="form.userName"
                type="text"
                class="input-field"
              />
            </div>
            <div class="field-group" style="flex: 1;">
              <label>userAccount</label>
              <input
                v-model="form.userAccount"
                type="text"
                class="input-field"
              />
            </div>
          </div>

          <div class="field-groups-row" style="display: flex; gap: 16px;">
            <div class="field-group" style="flex: 1;">
              <label>deviceCode</label>
              <input
                v-model="form.deviceCode"
                type="text"
                class="input-field"
              />
            </div>
            <div class="field-group" style="flex: 1;">
              <label>deviceName</label>
              <input
                v-model="form.deviceName"
                type="text"
                class="input-field"
              />
            </div>
          </div>

          <div class="section-title">定扭控制器参数设置</div>
          <div class="field-groups-row" style="display: flex; gap: 16px;">
            <div class="field-group" style="flex: 2;">
              <label>定扭控制器 IP (Desoutter Open Protocol)</label>
              <input
                v-model="form.desoutterIp"
                type="text"
                placeholder="192.168.5.212"
                class="input-field"
              />
            </div>
            <div class="field-group" style="flex: 1;">
              <label>端口</label>
              <input
                v-model.number="form.desoutterPort"
                type="number"
                placeholder="4545"
                class="input-field"
              />
            </div>
          </div>

          <div class="section-title">扫码枪参数设置</div>
          <div class="field-groups-row" style="display: flex; gap: 16px;">
            <div class="field-group" style="flex: 2;">
              <label>扫码枪 IP (Scanner TCP)</label>
              <input
                v-model="form.scannerIp"
                type="text"
                placeholder="192.168.x.x"
                class="input-field"
              />
            </div>
            <div class="field-group" style="flex: 1;">
              <label>扫码枪端口</label>
              <input
                v-model.number="form.scannerPort"
                type="number"
                placeholder="2000"
                class="input-field"
              />
            </div>
          </div>

          <div class="field-group">
            <label>扫码条码正则 (barcodeRegex)</label>
            <input
              v-model="form.barcodeRegex"
              type="text"
              placeholder=".*"
              class="input-field"
            />
          </div>
        </div>

        <div class="modal-footer">
          <button class="btn-cancel" @click="handleCancel">取消</button>
          <button class="btn-save" @click="handleSave">保存配置</button>
        </div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.65);
  backdrop-filter: blur(4px);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 99999;
}

.modal-panel {
  background: #1a1f2e;
  border: 1px solid rgba(100, 181, 246, 0.25);
  border-radius: 12px;
  width: 560px;
  max-width: 95vw;
  max-height: 90vh;
  box-shadow: 0 24px 64px rgba(0, 0, 0, 0.5);
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.modal-header {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 18px 24px;
  background: linear-gradient(135deg, #0d47a1 0%, #1565c0 100%);
  border-bottom: 1px solid rgba(100, 181, 246, 0.2);
}

.modal-header h2 {
  flex: 1;
  font-size: 16px;
  font-weight: 600;
  color: #e3f2fd;
  margin: 0;
}

.icon {
  font-size: 18px;
}

.close-btn {
  background: none;
  border: none;
  color: #90caf9;
  font-size: 16px;
  cursor: pointer;
  padding: 4px 8px;
  border-radius: 4px;
  transition: background 0.2s;
}

.close-btn:hover {
  background: rgba(255, 255, 255, 0.1);
}

.modal-body {
  padding: 24px;
  display: flex;
  flex-direction: column;
  gap: 20px;
  overflow-y: auto;
  min-height: 0;
}

.modal-body::-webkit-scrollbar {
  width: 6px;
}

.modal-body::-webkit-scrollbar-thumb {
  background: rgba(100, 181, 246, 0.35);
  border-radius: 3px;
}

.section-title {
  font-size: 12px;
  color: #64b5f6;
  font-weight: 700;
  letter-spacing: 0.5px;
  padding: 6px 10px;
  border-left: 3px solid #1976d2;
  background: rgba(25, 118, 210, 0.08);
  border-radius: 4px;
}

.field-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.field-group label {
  font-size: 13px;
  font-weight: 600;
  color: #90caf9;
  letter-spacing: 0.5px;
}

.field-group small {
  font-size: 11px;
  color: #546e7a;
}

.input-field {
  background: #0d1117;
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 6px;
  color: #e0e6ed;
  padding: 10px 14px;
  font-size: 13px;
  font-family: 'Consolas', monospace;
  transition: border-color 0.2s, box-shadow 0.2s;
  outline: none;
}

.input-field:focus {
  border-color: #42a5f5;
  box-shadow: 0 0 0 3px rgba(66, 165, 245, 0.15);
}

.input-field::placeholder {
  color: #37474f;
}

.dir-row {
  display: flex;
  gap: 10px;
}

.btn-pick {
  min-width: 92px;
  border-radius: 6px;
  border: 1px solid rgba(100, 181, 246, 0.3);
  background: rgba(100, 181, 246, 0.12);
  color: #90caf9;
  cursor: pointer;
  font-size: 12px;
}

.btn-pick:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.modal-footer {
  padding: 16px 24px;
  display: flex;
  gap: 12px;
  justify-content: flex-end;
  border-top: 1px solid rgba(100, 181, 246, 0.1);
}

.btn-cancel {
  padding: 9px 20px;
  background: transparent;
  border: 1px solid rgba(100, 181, 246, 0.25);
  border-radius: 6px;
  color: #78909c;
  font-size: 13px;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-cancel:hover {
  border-color: #42a5f5;
  color: #42a5f5;
}

.btn-save {
  padding: 9px 24px;
  background: linear-gradient(135deg, #1565c0, #0d47a1);
  border: 1px solid rgba(100, 181, 246, 0.3);
  border-radius: 6px;
  color: #e3f2fd;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-save:hover {
  background: linear-gradient(135deg, #1976d2, #1565c0);
  box-shadow: 0 4px 16px rgba(21, 101, 192, 0.4);
}

.modal-enter-active,
.modal-leave-active {
  transition: opacity 0.25s ease;
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
}

.modal-enter-active .modal-panel,
.modal-leave-active .modal-panel {
  transition: transform 0.25s ease;
}

.modal-enter-from .modal-panel {
  transform: scale(0.92) translateY(-20px);
}

.modal-leave-to .modal-panel {
  transform: scale(0.92) translateY(-20px);
}
</style>
