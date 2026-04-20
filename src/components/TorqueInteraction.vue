<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { TighteningTask } from '../types/mes'

const props = defineProps<{
  ip: string
  port: number
  tasks: TighteningTask[]
}>()

const emit = defineEmits<{
  (e: 'log', level: 'info'|'success'|'warn'|'error', msg: string): void
  (e: 'update:tasks', tasks: TighteningTask[]): void
  (e: 'taskFailed', task: TighteningTask): void
  (e: 'allTasksComplete'): void
}>()

// ==================== 状态管理 ====================
const isSignalRConnected = ref(false)
const isConnected = ref(false) // 对应控制器连接状态
const currentPSet = ref<string>('-')
const currentTorque = ref<string>('0.000')
const currentAngle = ref<string>('0.0')
const statusText = ref('等待连接控制器...')
const workflowStatus = ref('等待流程开启...')

const isHeartbeatActive = ref(false)
const isPSetSet = ref(false)
const isToolEnabled = ref(false)
const isDataSubscribed = ref(false)

const targetPSet = ref('001')
let connection: any = null
const isPreparingTask = ref(false)
const armedTaskId = ref<string | null>(null)

// 流程中止标志：只有用户点复位时才真正停止等待
const workflowAborted = ref(false)

onMounted(() => {
  initSignalR()
})

onUnmounted(() => {
  if (connection) connection.stop()
})

async function initSignalR() {
  connection = new HubConnectionBuilder()
    .withUrl('/api/torqueHub') 
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Information)
    .build()

  connection.on('ReceiveLog', (entry: any) => {
    if (entry.level === 'success' && (entry.msg.includes('[RX]') || entry.msg.includes('TCP 链路已连通'))) {
       statusText.value = '通信已建立 (已连接)'
       isConnected.value = true
       if (props.tasks.some(t => t.result === 'PENDING')) {
         void executeNextPendingTask()
       }
    }


    if (entry.isHeartbeat) {
      logHeartbeat(entry.level, entry.msg)
      isHeartbeatActive.value = false
      setTimeout(() => isHeartbeatActive.value = true, 100)
    } else {
      logLocal(entry.level, entry.msg)
    }
  })

  // 后端通知：SignalR 刚建立时，TCP 已连接 → 前端立即亮绿灯，并订阅拧紧数据
  connection.on('ControllerConnected', async () => {
    isConnected.value = true
    statusText.value = 'TCP 已连接，等待工步下发...'
    logLocal('success', '[System] ✅ 控制器 TCP 连接状态已同步')
    void executeNextPendingTask()
  })

  // 【核心交互】：处理拧紧结果并自动填入任务矩阵
  connection.on('ReceiveStatus', (status: string) => {
    if (status.includes('物理成功连上控制器') || status.includes('已连接')) {
      isConnected.value = true
      if (props.tasks.some(t => t.result === 'PENDING')) {
        void executeNextPendingTask()
      }
    } else if (status.includes('连接断开') || status.includes('未连接')) {
      isConnected.value = false
      isPSetSet.value = false
      isToolEnabled.value = false
      isDataSubscribed.value = false
      armedTaskId.value = null
    }
    logLocal('info', `[System] ${status}`)
  })

  connection.on('ReceiveData', (data: any) => {
    armedTaskId.value = null
    currentTorque.value = data.torque
    currentAngle.value = data.angle
    statusText.value = `收到拧紧结果: ${data.status}`

    // 复制一份任务列表进行更新
    const updatedTasks = JSON.parse(JSON.stringify(props.tasks)) as TighteningTask[]
    
    // 逻辑：寻找当前所有任务中，第一个结果为 PENDING 的任务
    const nextIdx = updatedTasks.findIndex(t => t.result === 'PENDING')
    if (nextIdx !== -1) {
      const task = updatedTasks[nextIdx]
      const now = new Date()
      const timestamp = `${now.toLocaleDateString()} ${now.toLocaleTimeString()}`
      
      // 直接根据控制器返回的 status 判定结果，不再根据上下限判定
      const isPass = (data.status === 'OK')

      // 保存到历史记录
      task.history.push({
        torque: data.torque,
        angle: data.angle,
        result: isPass ? 'PASS' : 'FAIL',
        timestamp: timestamp
      })

      // 更新主字段
      task.actualTorque = data.torque
      task.actualAngle = data.angle
      task.timestamp = timestamp
      task.result = isPass ? 'PASS' : 'FAIL'

      // 发射更新
      emit('update:tasks', updatedTasks)
      logLocal(isPass ? 'success' : 'error', `[数据] ${task.itemDisplayName}: ${data.torque}Nm / ${data.angle}Deg [${isPass ? 'PASS' : 'FAIL'}]`)

      // 触发后续自动化流程
      handleTaskResult(task)
    } else {
      emit('update:tasks', updatedTasks)
    }
  })

  try {
    await connection.start()
    isSignalRConnected.value = true 
    logLocal('success', '[System] 已连接到 C# 实时通讯服务')
    
    // 启动后尝试查询一次后端状态（可选，但目前我们依赖后端推送）
  } catch (err: any) {
    isConnected.value = false
    logLocal('error', '[System] 无法连接到 C# 服务')
  }
}

async function sendCommandToBackend(mid: string, pset: string = "") {
  try {
    await fetch(`http://127.0.0.1:5246/command?mid=${mid}&pset=${pset}`, { 
      method: 'POST' 
    })
  } catch (err) {

    logLocal('error', '[System] 指令下发失败，请求后端 API 异常')
  }
}

interface LocalLog {
  time: string
  level: string
  msg: string
}
const localLogs = ref<LocalLog[]>([])
const heartbeatLogs = ref<LocalLog[]>([])

function stringToHex(str: string): string {
  const parts: string[] = []
  const cleanStr = str.replace(/\\0/g, '\0')
  for (let i = 0; i < cleanStr.length; i++) {
    parts.push(cleanStr.charCodeAt(i).toString(16).toUpperCase().padStart(2, '0'))
  }
  return parts.join(' ')
}

function logLocal(level: 'info'|'success'|'warn'|'error', msg: string) {
  const time = new Date().toLocaleTimeString()
  let displayMsg = msg
  if (msg.startsWith('[TX] ') || msg.startsWith('[RX] ')) {
    const prefix = msg.slice(0, 5)
    const content = msg.slice(5)
    displayMsg = `${prefix}${stringToHex(content)}`
  }
  localLogs.value.unshift({ time, level, msg: displayMsg })
  if (localLogs.value.length > 50) localLogs.value.pop()
}

function logHeartbeat(level: 'info'|'success', msg: string) {
  const time = new Date().toLocaleTimeString()
  let displayMsg = msg
  if (msg.startsWith('[TX] ') || msg.startsWith('[RX] ')) {
    const prefix = msg.slice(0, 5)
    const content = msg.slice(5)
    displayMsg = `${prefix}${stringToHex(content)}`
  }
  heartbeatLogs.value.unshift({ time, level, msg: displayMsg })
  if (heartbeatLogs.value.length > 20) heartbeatLogs.value.pop()
}

async function handleConnect() {
  logLocal('info', `[System] 请求后端建立 TCP 连接...`)
  try {
    await fetch('http://127.0.0.1:5246/reconnect', { method: 'POST' })
    logLocal('info', '[System] 已通知后端发起 MID 0001 握手')
    if (props.tasks.some(t => t.result === 'PENDING')) {
      void executeNextPendingTask()
    }
  } catch (err) {

    logLocal('error', '[System] 通知后端失败，请确认后端服务已启动')
  }
}

function handleDisconnect() {
  // 断开业务逻辑
}

async function sendPSet() {
  const psetVal = targetPSet.value.toString().padStart(3, '0')
  await sendCommandToBackend('0018', psetVal)
  statusText.value = `正在下载 PSet ${psetVal}...`
  isPSetSet.value = true
}

async function enableTool() {
  await sendCommandToBackend('0043')
  statusText.value = `正在解锁工具...`
  isToolEnabled.value = true
}

async function lockTool() {
  await sendCommandToBackend('0042')
  statusText.value = `正在强制锁定工具...`
  isToolEnabled.value = false
}


// =============== 自动化流程方法 ===============

async function executeNextPendingTask() {
  if (isPreparingTask.value) return

  // 【等待连接】：TCP 未连接时轮询等待，而非中止流程
  if (!isConnected.value) {
    logLocal('warn', '[流程] ⏳ 控制器未连接，等待连接中...')
    statusText.value = '⏳ 等待控制器连接中...'
    // 每500ms检查一次，直到连上或流程被复位
    await new Promise<void>((resolve) => {
      const timer = setInterval(() => {
        if (workflowAborted.value || isConnected.value) {
          clearInterval(timer)
          resolve()
        }
      }, 500)
    })
    if (workflowAborted.value) {
      logLocal('info', '[流程] 流程已被复位，停止等待。')
      return
    }
    logLocal('success', '[流程] ✅ 控制器已连接，继续执行！')
  }

  const nextIdx = props.tasks.findIndex(t => t.result === 'PENDING')
  if (nextIdx === -1) {
    workflowStatus.value = '✅ 当前所有螺丝定扭任务已完成！'
    logLocal('success', '所有定扭任务已完成')
    emit('allTasksComplete')
    return
  }
  
  const task = props.tasks[nextIdx]
  if (armedTaskId.value === task.id) {
    statusText.value = `就绪，等待拧紧结果... (PSet ${currentPSet.value})`
    workflowStatus.value = `正在等待控制器上报: ${task.itemDisplayName} (${task.workstepName})`
    return
  }

  currentPSet.value = task.pSetNo
  workflowStatus.value = `正在进行: ${task.itemDisplayName} (${task.workstepName})`
  
  const psetVal = task.pSetNo.padStart(3, '0')
  
  isPreparingTask.value = true
  try {
    logLocal('info', `[流程] 准备执行螺丝: ${task.itemDisplayName}, PSet: ${psetVal}`)
    targetPSet.value = psetVal
    await sendCommandToBackend('0018', psetVal)
    isPSetSet.value = true
    statusText.value = `已下发 PSet ${psetVal}`
    
    await new Promise(r => setTimeout(r, 200))
    
    await sendCommandToBackend('0043')
    isToolEnabled.value = true
    statusText.value = `工具已使能，正在订阅数据...`
    
    if (!isDataSubscribed.value) {
      await sendCommandToBackend('0060')
      isDataSubscribed.value = true
    }
    armedTaskId.value = task.id
    workflowStatus.value = `正在等待控制器上报: ${task.itemDisplayName} (${task.workstepName})`
    statusText.value = `就绪，等待拧紧结果... (PSet ${psetVal})`
  } finally {
    isPreparingTask.value = false
  }
}

async function handleTaskResult(task: TighteningTask) {
  // 1. 打完立刻下发 0042 锁定工具 (Disable Tool)，防止误连打
  await sendCommandToBackend('0042')
  isToolEnabled.value = false
  statusText.value = `工具已锁定`
  logLocal('info', `[流程] 收到结果，立刻下发 0042 锁定工具`)
  
  if (task.result === 'FAIL') {
    // 2. 失败情况：保持锁定，等待人工确认
    logLocal('warn', `[流程] 拧紧失败，工具保持锁定，等待人工干预...`)
    emit('taskFailed', task)
  } else {
    // 3. 成功情况：等待 2s 后切换下一颗并解锁
    logLocal('info', `[流程] 拧紧成功，等待 2s 后继续...`)
    await new Promise(r => setTimeout(r, 2000))
    executeNextPendingTask()
  }
}


// 暴露方法给外部调用（如 MaterialScanner 验证完毕后启动，或人工确认失败后继续）
defineExpose({
  executeNextPendingTask,
  abortWorkflow: () => {
    workflowAborted.value = true
    logLocal('info', '[流程] 用户已复位，流程终止。')
    statusText.value = '已复位，等待下一件'
    workflowStatus.value = '等待流程开启...'
    currentPSet.value = '-'
    isPreparingTask.value = false
    armedTaskId.value = null
    isPSetSet.value = false
    isToolEnabled.value = false
    isDataSubscribed.value = false
  },
  resumeTighteningWorkflow: async () => {
    logLocal('info', `[流程] 人工确认完毕，等待 2s 后继续...`)
    await new Promise(r => setTimeout(r, 2000))
    executeNextPendingTask()
  },
  resetWorkflow: () => {
    workflowAborted.value = false
    isPreparingTask.value = false
    armedTaskId.value = null
    isPSetSet.value = false
    isToolEnabled.value = false
    isDataSubscribed.value = false
  }

})

</script>

<template>
  <div class="torque-panel">
    <div class="header">
      <span class="icon">🔧</span> 定扭控制器对接 (Desoutter Open Protocol)
    </div>

    <!-- 顶栏连接状态区域 -->
    <div class="status-bar" :class="{ connected: isConnected }">
      <div class="conn-info">
        <div class="ip-row">
          <span class="label">目标控制器：</span>
          <span class="mono ip-text">{{ ip }}:{{ port }}</span>
        </div>
        <div class="status-row">
          <div class="indicator" :class="{ 'on': isConnected }"></div>
          <span>{{ statusText }}</span>
        </div>
      </div>
      <div class="actions">
        <button v-if="!isConnected" class="btn connect" @click="handleConnect" :disabled="!isSignalRConnected">🔗 连接接口 (MID 0001)</button>
        <button v-else class="btn disconnect" @click="handleDisconnect">🔌 断开连接 (MID 0003)</button>
      </div>
    </div>
    
    <!-- 自动化流程状态栏 -->
    <div class="workflow-bar" v-if="isConnected">
      <div class="wf-content">
        <span class="wf-icon">🚀</span>
        <span class="wf-text">{{ workflowStatus }}</span>
      </div>
    </div>

    <div class="dashboard" :class="{ 'dimmed': !isConnected }">
      <!-- 状态指示灯 -->
      <div class="state-flags">
        <div class="flag" :class="{ on: isHeartbeatActive }">
          <div class="flag-led"></div>心跳信号(MID 9999)
        </div>
        <div class="flag" :class="{ on: isPSetSet }">
          <div class="flag-led"></div>程序设定(MID 0019)
        </div>
        <div class="flag" :class="{ on: isToolEnabled }">
          <div class="flag-led"></div>枪体使能(MID 0044)
        </div>
        <div class="flag" :class="{ on: isDataSubscribed }">
          <div class="flag-led"></div>实时数据(MID 0060)
        </div>
      </div>

      <!-- 实时数据看板 -->
      <div class="dashboard-metrics">
        <div class="metric-card">
          <div class="m-label">当前 PSet</div>
          <div class="m-val mono">{{ currentPSet }}</div>
        </div>
        <div class="metric-card highlight">
          <div class="m-label">实时扭矩 (Nm)</div>
          <div class="m-val mono green">{{ currentTorque }}</div>
        </div>
        <div class="metric-card highlight">
          <div class="m-label">实时角度 (deg)</div>
          <div class="m-val mono green">{{ currentAngle }}</div>
        </div>
      </div>

      <!-- 操作面板 -->
      <div class="controls-panel">
        <h3 class="panel-title">快捷指令下发及测试</h3>
        <div class="cmd-buttons">
          <div class="pset-input-wrap">
            <span class="pset-label">PSet编号:</span>
            <input type="text" v-model="targetPSet" class="pset-input mono" placeholder="001" maxlength="3">
          </div>
          <button class="btn cmd" @click="sendPSet" :disabled="!isConnected">
            📥 PSet 设置 (MID 0018)
          </button>
          <button class="btn cmd primary" @click="enableTool" :disabled="!isConnected">
            🔓 枪体使能 (MID 0043)
          </button>
          <button class="btn cmd lock" @click="lockTool" :disabled="!isConnected">
            🔒 锁定工具 (MID 0042)
          </button>
        </div>




        <div class="logs-container">
          <!-- 主通信日志 -->
          <div class="terminal-panel flex-2">
            <div class="terminal-header">
              <span>实时通讯日志 (Open Protocol 消息流)</span>
              <button class="clear-btn" @click="localLogs = []">清空</button>
            </div>
            <div class="terminal-content">
              <div 
                v-for="(log, idx) in localLogs" 
                :key="idx" 
                class="t-log"
                :class="log.level"
              >
                <span class="t-time">[{{ log.time }}]</span>
                <span class="t-msg">{{ log.msg }}</span>
              </div>
              <div v-if="!localLogs.length" class="t-empty">暂无交互日志...</div>
            </div>
          </div>

          <!-- 心跳专用独立日志 -->
          <div class="terminal-panel heartbeat-panel">
            <div class="terminal-header">
              <span>心跳专属保活日志 (Keep-Alive)</span>
            </div>
            <div class="terminal-content">
              <div 
                v-for="(log, idx) in heartbeatLogs" 
                :key="'hb-'+idx" 
                class="t-log"
                :class="log.level"
              >
                <span class="t-time">[{{ log.time }}]</span>
                <span class="t-msg">{{ log.msg }}</span>
              </div>
              <div v-if="!heartbeatLogs.length" class="t-empty">等待心跳启动...</div>
            </div>
          </div>
        </div>

      </div>
    </div>

  </div>
</template>

<style scoped>
.torque-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: #111520;
  border-radius: 8px;
  border: 1px solid rgba(171, 71, 188, 0.15);
}

.header {
  padding: 12px 16px;
  background: linear-gradient(90deg, rgba(142, 36, 170, 0.3), transparent);
  border-bottom: 1px solid rgba(171, 71, 188, 0.15);
  font-weight: 600;
  color: #e1bee7;
  display: flex;
  align-items: center;
  gap: 8px;
}

.status-bar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px;
  background: #0d1117;
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
}

.status-bar.connected {
  background: rgba(0, 230, 118, 0.03);
}

.conn-info {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.ip-row {
  font-size: 13px;
  color: #cfd8dc;
}
.ip-text {
  color: #64b5f6;
  font-weight: bold;
}

.status-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  color: #90caf9;
}

.indicator {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: #f44336;
  box-shadow: 0 0 8px rgba(244, 67, 54, 0.5);
}
.indicator.on {
  background: #00e676;
  box-shadow: 0 0 8px rgba(0, 230, 118, 0.5);
}

.actions {
  display: flex;
  gap: 12px;
}

.btn {
  border: none;
  border-radius: 6px;
  padding: 8px 16px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.2s;
}
.btn:disabled { opacity: 0.5; cursor: not-allowed; }

.btn.connect {
  background: rgba(66, 165, 245, 0.15);
  color: #64b5f6;
  border: 1px solid rgba(66, 165, 245, 0.3);
}
.btn.connect:hover { background: rgba(66, 165, 245, 0.25); }

.btn.disconnect {
  background: rgba(244, 67, 54, 0.15);
  color: #e57373;
  border: 1px solid rgba(244, 67, 54, 0.3);
}

.dashboard {
  flex: 1;
  padding: 24px;
  display: flex;
  flex-direction: column;
  gap: 24px;
  transition: opacity 0.3s;
}
.dashboard.dimmed { opacity: 0.3; pointer-events: none; }

.dashboard-metrics {
  display: flex;
  gap: 16px;
}

.metric-card {
  flex: 1;
  background: rgba(100, 181, 246, 0.05);
  border: 1px solid rgba(100, 181, 246, 0.1);
  border-radius: 8px;
  padding: 20px;
  text-align: center;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
}

.metric-card.highlight {
  background: rgba(0, 230, 118, 0.03);
  border-color: rgba(0, 230, 118, 0.15);
}

.m-label {
  font-size: 12px;
  color: #78909c;
  font-weight: 600;
}

.m-val {
  font-size: 36px;
  font-weight: 700;
  color: #e0e6ed;
}

.m-val.green {
  color: #00e676;
  text-shadow: 0 0 16px rgba(0, 230, 118, 0.2);
}

.workflow-bar {
  margin-bottom: 20px;
  background: rgba(100, 181, 246, 0.08);
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 8px;
  padding: 12px 16px;
  border-left: 4px solid #64b5f6;
}
.wf-content {
  display: flex;
  align-items: center;
  gap: 12px;
}
.wf-icon {
  font-size: 18px;
  animation: bounce 2s infinite;
}
.wf-text {
  font-size: 15px;
  font-weight: 600;
  color: #e3f2fd;
  letter-spacing: 0.5px;
}

@keyframes bounce {
  0%, 20%, 50%, 80%, 100% {transform: translateY(0);}
  40% {transform: translateY(-5px);}
  60% {transform: translateY(-3px);}
}


.controls-panel {
  margin-top: auto;
  border-top: 1px solid rgba(100, 181, 246, 0.1);
  padding-top: 20px;
}

.panel-title {
  margin: 0 0 16px 0;
  font-size: 14px;
  color: #b0bec5;
}

.cmd-buttons {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-bottom: 24px;
  align-items: center;
}

.pset-input-wrap {
  display: flex;
  align-items: center;
  gap: 8px;
  background: rgba(255, 255, 255, 0.05);
  padding: 6px 14px;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  box-shadow: inset 0 2px 4px rgba(0, 0, 0, 0.2);
}
.pset-label {
  font-size: 13px;
  color: #90a4ae;
  font-weight: 500;
}
.pset-input {
  background: transparent;
  border: none;
  border-bottom: 2px solid #64b5f6;
  color: #00e676;
  width: 50px;
  text-align: center;
  font-size: 18px;
  font-weight: bold;
  outline: none;
  transition: all 0.2s;
}
.pset-input:focus {
  border-bottom-color: #00c853;
  background: rgba(0, 230, 118, 0.05);
}

.btn.cmd {
  background: rgba(142, 36, 170, 0.15);
  color: #ce93d8;
  border: 1px solid rgba(142, 36, 170, 0.3);
}

.btn.cmd.lock {
  background: rgba(244, 67, 54, 0.2);
  color: #ef9a9a;
  border: 1px solid rgba(244, 67, 54, 0.4);
}

.btn.cmd.lock:hover {
  background: rgba(244, 67, 54, 0.3);
}

.btn.cmd.primary {
  background: linear-gradient(135deg, #1565c0, #0d47a1);
  color: white;
  border: none;
}

.btn.cmd.outline {
  background: transparent;
  color: #64b5f6;
  border: 1px dashed rgba(66, 165, 245, 0.5);
}
.btn.cmd.outline:hover {
  background: rgba(66, 165, 245, 0.1);
}

.state-flags {
  display: flex;
  justify-content: space-between;
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.05);
  padding: 16px;
  border-radius: 8px;
}

.flag {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  color: #78909c;
  transition: color 0.3s;
}

.flag-led {
  width: 14px;
  height: 14px;
  border-radius: 50%;
  background: #37474f;
  box-shadow: inset 0 2px 4px rgba(0,0,0,0.5);
  transition: all 0.3s;
}

.flag.on {
  color: #e0e6ed;
  font-weight: 600;
}

.flag.on .flag-led {
  background: #00e676;
  box-shadow: 0 0 10px rgba(0, 230, 118, 0.5), inset 0 1px 2px rgba(255,255,255,0.5);
}

.info-note {
  font-size: 12px;
  color: #78909c;
  line-height: 1.5;
  background: rgba(0, 0, 0, 0.2);
  padding: 12px;
  border-radius: 6px;
  border-left: 3px solid #64b5f6;
}

.logs-container {
  display: flex;
  gap: 16px;
  margin-top: 16px;
}

.terminal-panel {
  background: #0d1117;
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 8px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  max-height: 200px;
}
.terminal-panel.flex-2 {
  flex: 2;
}
.terminal-panel.heartbeat-panel {
  flex: 1;
  border-color: rgba(0, 230, 118, 0.2);
}
.heartbeat-panel .terminal-header {
  background: rgba(0, 230, 118, 0.05);
  border-bottom-color: rgba(0, 230, 118, 0.1);
  color: #69f0ae;
}

.terminal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 8px 12px;
  background: rgba(100, 181, 246, 0.05);
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  font-size: 12px;
  color: #90caf9;
  font-weight: 600;
}

.clear-btn {
  background: none;
  border: 1px solid rgba(244, 67, 54, 0.3);
  color: #e57373;
  border-radius: 4px;
  font-size: 11px;
  cursor: pointer;
  padding: 2px 8px;
  transition: all 0.2s;
}
.clear-btn:hover {
  background: rgba(244, 67, 54, 0.1);
}

.terminal-content {
  padding: 8px;
  overflow-y: auto;
  font-family: 'Consolas', monospace;
  font-size: 12px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.t-log {
  display: flex;
  gap: 8px;
  padding: 4px 6px;
  border-radius: 4px;
}
.t-log:hover {
  background: rgba(255, 255, 255, 0.03);
}
.t-time {
  color: #78909c;
  flex-shrink: 0;
}
.t-msg {
  word-break: break-all;
  white-space: pre-wrap;
}

.t-log.info .t-msg { color: #e0e6ed; }
.t-log.success .t-msg { color: #00e676; }
.t-log.warn .t-msg { color: #ffb300; }
.t-log.error .t-msg { color: #f44336; }

.t-empty {
  color: #546e7a;
  text-align: center;
  padding: 16px;
  font-style: italic;
}

.mono { font-family: 'Consolas', monospace; }
</style>
