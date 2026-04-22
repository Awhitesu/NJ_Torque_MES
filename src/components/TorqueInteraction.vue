<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { TighteningTask } from '../types/mes'

type UiLogLevel = 'info' | 'success' | 'warn' | 'error'

interface LocalLog {
  time: string
  level: UiLogLevel
  msg: string
}

const props = defineProps<{
  ip: string
  port: number
  tasks: TighteningTask[]
  manualCommandAuthorized: boolean
}>()

const emit = defineEmits<{
  (e: 'log', level: UiLogLevel, msg: string): void
  (e: 'update:tasks', tasks: TighteningTask[]): void
  (e: 'taskFailed', task: TighteningTask): void
  (e: 'allTasksComplete'): void
  (e: 'request-manual-auth'): void
}>()

const isSignalRConnected = ref(false)
const isConnected = ref(false)
const lastControllerStatus = ref('')
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
const isPreparingTask = ref(false)
const armedTaskId = ref<string | null>(null)
const workflowAborted = ref(false)

const backendBaseUrl = 'http://127.0.0.1:5246'

let connection: any = null

const localLogs = ref<LocalLog[]>([])
const heartbeatLogs = ref<LocalLog[]>([])

interface CommandAckResult {
  rxMid: string
  targetMid: string
  raw: string
  errorCode?: string
}

interface CommandWaiter {
  targetMid: string
  expectedRxMids: Set<string>
  resolve: (result: CommandAckResult) => void
  reject: (reason?: unknown) => void
  timer: ReturnType<typeof setTimeout>
}

const commandWaiters = new Map<string, CommandWaiter>()
let commandQueue: Promise<void> = Promise.resolve()

onMounted(() => {
  void initSignalR()
})

onUnmounted(() => {
  for (const [mid, waiter] of commandWaiters.entries()) {
    clearTimeout(waiter.timer)
    waiter.reject(new Error(`MID ${mid} waiter canceled`))
  }
  commandWaiters.clear()
  if (connection) {
    void connection.stop()
  }
})

watch(
  () => [props.ip, props.port] as const,
  async () => {
    if (!isSignalRConnected.value) return
    try {
      await syncControllerConfig(false)
      logLocal('info', `[System] Controller endpoint updated: ${props.ip}:${props.port}`)
    } catch (err: any) {
      logLocal('warn', `[System] Controller endpoint sync failed: ${err?.message || err}`)
    }
  }
)

function normalizeLevel(raw: unknown): UiLogLevel {
  if (raw === 'success' || raw === 'warn' || raw === 'error') return raw
  return 'info'
}

async function initSignalR() {
  connection = new HubConnectionBuilder()
    .withUrl('/api/torqueHub')
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Information)
    .build()

  connection.on('ReceiveLog', (entry: any) => {
    const msg = String(entry?.msg || '')
    const level = normalizeLevel(entry?.level)

    if (entry?.isHeartbeat) {
      const heartbeatLevel: 'info' | 'success' = level === 'success' ? 'success' : 'info'
      logHeartbeat(heartbeatLevel, msg)
      isHeartbeatActive.value = false
      setTimeout(() => {
        isHeartbeatActive.value = true
      }, 100)
      return
    }

    if (msg.startsWith('[RX] ')) {
      const rawPayload = msg.slice(5)
      handleRxPayloadForAck(rawPayload)
    }

    logLocal(level, msg)
  })

  connection.on('ControllerConnected', () => {
    logLocal('info', '[System] 收到控制器连接事件，等待状态确认...')
  })

  connection.on('ReceiveStatus', (statusRaw: string) => {
    const status = String(statusRaw || '').trim()
    if (!status || status === lastControllerStatus.value) return
    lastControllerStatus.value = status

    if (status.includes('已连接') || status.includes('成功')) {
      isConnected.value = true
      if (props.tasks.some(t => t.result === 'PENDING')) {
        void executeNextPendingTask()
      }
    } else if (status.includes('断开') || status.includes('未连接')) {
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

    const torque = String(data?.torque ?? '0.000')
    const angle = String(data?.angle ?? '0.0')
    const status = String(data?.status ?? 'NOK')

    currentTorque.value = torque
    currentAngle.value = angle
    statusText.value = `收到拧紧结果: ${status}`

    const updatedTasks = JSON.parse(JSON.stringify(props.tasks)) as TighteningTask[]
    const nextIdx = updatedTasks.findIndex(t => t.result === 'PENDING')
    if (nextIdx === -1) {
      emit('update:tasks', updatedTasks)
      return
    }

    const task = updatedTasks[nextIdx]
    const now = new Date()
    const timestamp = `${now.toLocaleDateString()} ${now.toLocaleTimeString()}`
    const isPass = status === 'OK'

    task.history.push({
      torque,
      angle,
      result: isPass ? 'PASS' : 'FAIL',
      timestamp
    })

    task.actualTorque = torque
    task.actualAngle = angle
    task.timestamp = timestamp
    task.result = isPass ? 'PASS' : 'FAIL'

    emit('update:tasks', updatedTasks)
    logLocal(isPass ? 'success' : 'error', `[Data] ${task.itemDisplayName}: ${torque}Nm / ${angle}Deg [${isPass ? 'PASS' : 'FAIL'}]`)
    void handleTaskResult(task)
  })

  try {
    await connection.start()
    isSignalRConnected.value = true
    logLocal('success', '[System] SignalR connected to backend')
  } catch {
    isConnected.value = false
    logLocal('error', '[System] SignalR connection failed')
  }
}

async function sendCommandToBackend(mid: string, pset = '') {
  const response = await fetch(`${backendBaseUrl}/command?mid=${mid}&pset=${pset}`, {
    method: 'POST'
  })
  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `HTTP ${response.status}`)
  }
}

async function syncControllerConfig(reconnect: boolean) {
  const response = await fetch(`${backendBaseUrl}/controller/config`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ip: props.ip,
      port: props.port,
      reconnect
    })
  })

  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `HTTP ${response.status}`)
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms))
}

function enqueueCommand<T>(job: () => Promise<T>): Promise<T> {
  const run = commandQueue.then(job, job)
  commandQueue = run.then(() => undefined, () => undefined)
  return run
}

function clearCommandWaiter(targetMid: string, reason = 'replaced by new waiter') {
  const waiter = commandWaiters.get(targetMid)
  if (!waiter) return
  clearTimeout(waiter.timer)
  commandWaiters.delete(targetMid)
  waiter.reject(new Error(`MID ${targetMid} waiter canceled: ${reason}`))
}

function resolveCommandWaiter(targetMid: string, result: CommandAckResult) {
  const waiter = commandWaiters.get(targetMid)
  if (!waiter) return
  if (!waiter.expectedRxMids.has(result.rxMid)) return
  clearTimeout(waiter.timer)
  commandWaiters.delete(targetMid)
  waiter.resolve(result)
}

function rejectCommandWaiter(targetMid: string, error: Error) {
  const waiter = commandWaiters.get(targetMid)
  if (!waiter) return
  clearTimeout(waiter.timer)
  commandWaiters.delete(targetMid)
  waiter.reject(error)
}

function waitForCommandAck(targetMid: string, expectedRxMids: string[], timeoutMs = 3000): Promise<CommandAckResult> {
  clearCommandWaiter(targetMid)
  return new Promise<CommandAckResult>((resolve, reject) => {
    const timer = setTimeout(() => {
      commandWaiters.delete(targetMid)
      reject(new Error(`MID ${targetMid} ack timeout (${timeoutMs}ms)`))
    }, timeoutMs)
    commandWaiters.set(targetMid, {
      targetMid,
      expectedRxMids: new Set(expectedRxMids),
      resolve,
      reject,
      timer
    })
  })
}

async function sendCommandAndWaitAck(options: {
  mid: string
  pset?: string
  targetMid?: string
  expectedRxMids: string[]
  timeoutMs?: number
}) {
  return enqueueCommand(async () => {
    const targetMid = options.targetMid || options.mid
    const ackPromise = waitForCommandAck(targetMid, options.expectedRxMids, options.timeoutMs ?? 3000)
    await sendCommandToBackend(options.mid, options.pset || '')
    return ackPromise
  })
}

async function sendPSetWithRetry(psetVal: string, context: '手动' | '流程') {
  const maxAttempts = 2
  let lastErr: unknown = null

  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      await sendCommandAndWaitAck({
        mid: '0018',
        pset: psetVal,
        targetMid: '0018',
        expectedRxMids: ['0005', '0019'],
        timeoutMs: 5000
      })
      if (attempt > 1) {
        logLocal('success', `[${context}] PSet ${psetVal} 第${attempt}次下发确认成功`)
      }
      return
    } catch (err: any) {
      lastErr = err
      const msg = err?.message || String(err)
      const shouldRetry = attempt < maxAttempts && msg.includes('ack timeout')
      if (!shouldRetry) throw err

      logLocal('warn', `[${context}] PSet ${psetVal} 确认超时，准备重试 (${attempt}/${maxAttempts})`)
      await sleep(600)
    }
  }

  throw (lastErr instanceof Error ? lastErr : new Error(String(lastErr ?? 'PSet ack failed')))
}

function handleRxPayloadForAck(rawPayload: string) {
  const payload = normalizePayloadToAscii(rawPayload)
  if (!payload || payload.length < 8) return

  const rxMid = payload.slice(4, 8)
  if (rxMid === '0005' && payload.length >= 24) {
    const ackMid = payload.slice(20, 24).trim()
    if (!ackMid) return
    resolveCommandWaiter(ackMid, { rxMid, targetMid: ackMid, raw: payload })
    return
  }

  if (rxMid === '0004' && payload.length >= 26) {
    const failedMid = payload.slice(20, 24).trim()
    const errorCode = payload.slice(24, 26).trim()
    if (!failedMid) return

    // 0060 + 09 在该控制器上常见，表示已订阅/可忽略，按成功处理
    if (failedMid === '0060' && errorCode === '09') {
      resolveCommandWaiter('0060', { rxMid, targetMid: '0060', raw: payload, errorCode })
      return
    }

    rejectCommandWaiter(failedMid, new Error(`MID ${failedMid} rejected, code ${errorCode || '??'}`))
    return
  }

  if (rxMid === '0019') {
    resolveCommandWaiter('0018', { rxMid, targetMid: '0018', raw: payload })
    return
  }

  if (rxMid === '0044') {
    resolveCommandWaiter('0043', { rxMid, targetMid: '0043', raw: payload })
  }
}

function getMidDescription(mid: string): string {
  const table: Record<string, string> = {
    '0001': '通信启动',
    '0002': '通信启动应答',
    '0004': '命令错误',
    '0005': '命令接受',
    '0018': 'PSet 选择',
    '0019': 'PSet 选择应答',
    '0042': '工具锁定',
    '0043': '工具使能',
    '0044': '工具使能应答',
    '0060': '结果订阅',
    '0061': '结果数据',
    '0062': '结果确认',
    '9999': '心跳保活'
  }
  return table[mid] || '未知MID'
}

function normalizePayloadToAscii(payload: string): string {
  const trimmed = payload.trim()
  if (!trimmed) return payload

  const isHexBytes = /^(?:[0-9A-Fa-f]{2}(?:\s+|$))+$/.test(trimmed)
  if (!isHexBytes) return payload

  try {
    return trimmed
      .split(/\s+/)
      .map(part => String.fromCharCode(parseInt(part, 16)))
      .join('')
  } catch {
    return payload
  }
}

function formatTxRxMessage(msg: string): string {
  const isTxOrRx = msg.startsWith('[TX] ') || msg.startsWith('[RX] ')
  if (!isTxOrRx) return msg

  const prefix = msg.slice(0, 5)
  const payload = normalizePayloadToAscii(msg.slice(5))
  const ascii = payload.replace(/\0/g, '\\0').replace(/ /g, '·')

  const mid = payload.length >= 8 ? payload.slice(4, 8) : '----'
  let parsed = `MID ${mid} (${getMidDescription(mid)})`

  // MID 0005 报文的 20~23 位通常是被确认的命令 MID
  if (mid === '0005' && payload.length >= 24) {
    const ackMid = payload.slice(20, 24)
    parsed += ` -> ACK ${ackMid} (${getMidDescription(ackMid)})`
  }

  return `${prefix}ASCII:${ascii} | ${parsed}`
}

function logLocal(level: UiLogLevel, msg: string) {
  const time = new Date().toLocaleTimeString()
  const displayMsg = formatTxRxMessage(msg)

  localLogs.value.unshift({ time, level, msg: displayMsg })
  if (localLogs.value.length > 50) localLogs.value.pop()
}

function logHeartbeat(level: 'info' | 'success', msg: string) {
  const time = new Date().toLocaleTimeString()
  const displayMsg = formatTxRxMessage(msg)

  heartbeatLogs.value.unshift({ time, level, msg: displayMsg })
  if (heartbeatLogs.value.length > 20) heartbeatLogs.value.pop()
}

function requestManualAuthPrompt(logMessage = true) {
  emit('request-manual-auth')
  if (logMessage) {
    logLocal('warn', '[权限] 快捷指令已禁用，请先管理员登录授权')
  }
}

function ensureManualCommandAuthorized(): boolean {
  if (props.manualCommandAuthorized) return true
  requestManualAuthPrompt()
  return false
}

async function handleConnect() {
  logLocal('info', '[System] Request backend to connect controller...')
  try {
    workflowAborted.value = false
    await syncControllerConfig(true)
    logLocal('info', `[System] Reconnect requested: ${props.ip}:${props.port}`)
    if (props.tasks.some(t => t.result === 'PENDING')) {
      void executeNextPendingTask()
    }
  } catch {
    logLocal('error', '[System] Failed to notify backend for reconnect')
  }
}

async function handleDisconnect() {
  logLocal('info', '[System] Request backend to disconnect controller...')
  try {
    await fetch(`${backendBaseUrl}/disconnect`, { method: 'POST' })
    isConnected.value = false
    isPSetSet.value = false
    isToolEnabled.value = false
    isDataSubscribed.value = false
    armedTaskId.value = null
    statusText.value = '连接已断开'
    workflowStatus.value = '等待流程开启...'
    logLocal('warn', '[System] Controller disconnected')
  } catch {
    logLocal('error', '[System] Disconnect request failed')
  }
}

async function sendPSet() {
  if (!ensureManualCommandAuthorized()) return
  const psetVal = targetPSet.value.toString().padStart(3, '0')
  try {
    statusText.value = `正在下载 PSet ${psetVal}...`
    await sendPSetWithRetry(psetVal, '手动')
    isPSetSet.value = true
    logLocal('success', `[System] PSet ${psetVal} 已确认`)
  } catch (err: any) {
    isPSetSet.value = false
    statusText.value = `PSet 下发失败: ${err?.message || err}`
    logLocal('error', `[System] PSet 设置失败: ${err?.message || err}`)
  }
}

async function enableTool() {
  if (!ensureManualCommandAuthorized()) return
  try {
    statusText.value = '正在使能工具...'
    await sendCommandAndWaitAck({
      mid: '0043',
      targetMid: '0043',
      expectedRxMids: ['0005', '0044'],
      timeoutMs: 3500
    })
    isToolEnabled.value = true
    logLocal('success', '[System] 工具使能已确认')
  } catch (err: any) {
    isToolEnabled.value = false
    statusText.value = `工具使能失败: ${err?.message || err}`
    logLocal('error', `[System] 工具使能失败: ${err?.message || err}`)
  }
}

async function lockTool() {
  if (!ensureManualCommandAuthorized()) return
  try {
    statusText.value = '正在锁定工具...'
    await sendCommandAndWaitAck({
      mid: '0042',
      targetMid: '0042',
      expectedRxMids: ['0005'],
      timeoutMs: 3000
    })
    isToolEnabled.value = false
    logLocal('success', '[System] 工具锁定已确认')
  } catch (err: any) {
    statusText.value = `工具锁定失败: ${err?.message || err}`
    logLocal('error', `[System] 工具锁定失败: ${err?.message || err}`)
  }
}

async function executeNextPendingTask() {
  if (isPreparingTask.value) return

  if (!isConnected.value) {
    logLocal('warn', '[Flow] Controller is not connected, waiting...')
    statusText.value = '等待控制器连接中...'

    await new Promise<void>((resolve) => {
      const timer = setInterval(() => {
        if (workflowAborted.value || isConnected.value) {
          clearInterval(timer)
          resolve()
        }
      }, 500)
    })

    if (workflowAborted.value) {
      logLocal('info', '[Flow] Workflow aborted while waiting for connection')
      return
    }

    logLocal('success', '[Flow] Controller connected, continue workflow')
  }

  const nextIdx = props.tasks.findIndex(t => t.result === 'PENDING')
  if (nextIdx === -1) {
    workflowStatus.value = '当前所有螺丝定扭任务已完成'
    logLocal('success', 'All tightening tasks completed')
    emit('allTasksComplete')
    return
  }

  const task = props.tasks[nextIdx]
  if (armedTaskId.value === task.id) {
    statusText.value = `就绪，等待拧紧结果... (PSet ${currentPSet.value})`
    workflowStatus.value = `等待上报: ${task.itemDisplayName} (${task.workstepName})`
    return
  }

  currentPSet.value = task.pSetNo
  workflowStatus.value = `正在执行: ${task.itemDisplayName} (${task.workstepName})`

  const psetVal = task.pSetNo.padStart(3, '0')

  isPreparingTask.value = true
  try {
    logLocal('info', `[Flow] Prepare task ${task.itemDisplayName}, PSet ${psetVal}`)
    targetPSet.value = psetVal

    await sendPSetWithRetry(psetVal, '流程')
    isPSetSet.value = true
    statusText.value = `PSet 下发完成: ${psetVal}`

    await sleep(200)

    await sendCommandAndWaitAck({
      mid: '0043',
      targetMid: '0043',
      expectedRxMids: ['0005', '0044'],
      timeoutMs: 3500
    })
    isToolEnabled.value = true
    statusText.value = '工具已使能，准备订阅数据...'

    if (!isDataSubscribed.value) {
      await sendCommandAndWaitAck({
        mid: '0060',
        targetMid: '0060',
        expectedRxMids: ['0005', '0004'],
        timeoutMs: 3000
      })
      isDataSubscribed.value = true
    }

    armedTaskId.value = task.id
    workflowStatus.value = `等待上报: ${task.itemDisplayName} (${task.workstepName})`
    statusText.value = `就绪，等待拧紧结果... (PSet ${psetVal})`
  } catch (err: any) {
    armedTaskId.value = null
    isToolEnabled.value = false
    const msg = err?.message || String(err)
    workflowStatus.value = `命令链失败: ${task.itemDisplayName}`
    statusText.value = `等待人工处理: ${msg}`
    logLocal('error', `[Flow] Command chain failed (${task.itemDisplayName}): ${msg}`)
  } finally {
    isPreparingTask.value = false
  }
}

async function handleTaskResult(task: TighteningTask) {
  try {
    await sendCommandAndWaitAck({
      mid: '0042',
      targetMid: '0042',
      expectedRxMids: ['0005'],
      timeoutMs: 3000
    })
    isToolEnabled.value = false
    statusText.value = '工具已锁定'
    logLocal('info', '[Flow] Result received, lock tool immediately (MID 0042)')
  } catch (err: any) {
    const msg = err?.message || String(err)
    logLocal('error', `[Flow] Lock tool failed: ${msg}`)
  }

  if (task.result === 'FAIL') {
    statusText.value = '当前螺丝NOK，工具已锁定，等待人工确认...'
    workflowStatus.value = `人工处理中: ${task.itemDisplayName} (${task.workstepName})`
    logLocal('warn', '[Flow] Tightening failed, waiting for manual handling')
    emit('taskFailed', task)
    return
  }

  logLocal('info', '[Flow] Tightening passed, continue after 2s')
  await sleep(2000)
  void executeNextPendingTask()
}

defineExpose({
  executeNextPendingTask,
  abortWorkflow: () => {
    workflowAborted.value = true
    logLocal('info', '[Flow] Workflow aborted by reset action')
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
    logLocal('info', '[Flow] Manual confirmation done, continue after 2s')
    await sleep(2000)
    void executeNextPendingTask()
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

    <!-- 顶部连接状态 -->
    <div class="status-bar" :class="{ connected: isConnected }">
      <div class="conn-info">
        <div class="ip-row">
          <span class="label">目标控制器：</span>
          <span class="mono ip-text">{{ ip }}:{{ port }}</span>
          <span class="heartbeat-pill" :class="{ on: isHeartbeatActive }">
            <span class="heartbeat-led"></span>
            心跳
          </span>
        </div>
        <div class="status-row">
          <div class="indicator" :class="{ 'on': isConnected }"></div>
          <span>{{ statusText }}</span>
        </div>
      </div>
      <div class="actions">
        <button v-if="!isConnected" class="btn connect" @click="handleConnect" :disabled="!isSignalRConnected">🔌 连接控制器 (MID 0001)</button>
        <button v-else class="btn disconnect" @click="handleDisconnect">🔒 断开连接 (MID 0003)</button>
      </div>
    </div>
    
    <!-- 自动流程状态栏 -->
    <div class="workflow-bar" v-if="isConnected">
      <div class="wf-content">
        <span class="wf-icon">🚀</span>
        <span class="wf-text">{{ workflowStatus }}</span>
      </div>
    </div>

    <div class="dashboard" :class="{ 'dimmed': !isConnected }">
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
        <h3 class="panel-title">快捷指令下发与测试</h3>
        <div class="cmd-buttons">
          <div class="auth-inline">
            <button
              v-if="!props.manualCommandAuthorized"
              class="btn auth-combo"
              @click="requestManualAuthPrompt(false)"
            >
              快捷下发已禁用，点击管理员登录授权
            </button>
            <span v-else class="auth-tag authorized">管理员已授权，可手动下发</span>
          </div>
          <div class="pset-input-wrap">
            <span class="pset-label">PSet 编号:</span>
            <input
              type="text"
              v-model="targetPSet"
              class="pset-input mono"
              placeholder="001"
              maxlength="3"
              :disabled="!isConnected || !props.manualCommandAuthorized"
            >
          </div>
          <button class="btn cmd" @click="sendPSet" :disabled="!isConnected || !props.manualCommandAuthorized">
            📜 PSet 设置 (MID 0018)
          </button>
          <button class="btn cmd primary" @click="enableTool" :disabled="!isConnected || !props.manualCommandAuthorized">
            🔓 工具使能 (MID 0043)
          </button>
          <button class="btn cmd lock" @click="lockTool" :disabled="!isConnected || !props.manualCommandAuthorized">
            🔒 工具锁定 (MID 0042)
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

          <!-- 心跳专用日志 -->
          <div class="terminal-panel heartbeat-panel">
            <div class="terminal-header">
              <span>心跳保活日志 (Keep-Alive)</span>
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
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 13px;
  color: #cfd8dc;
}
.ip-text {
  color: #64b5f6;
  font-weight: bold;
}

.heartbeat-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 2px 10px;
  border-radius: 999px;
  border: 1px solid rgba(100, 181, 246, 0.25);
  background: rgba(100, 181, 246, 0.08);
  color: #90caf9;
  font-size: 12px;
}

.heartbeat-led {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: #37474f;
  box-shadow: inset 0 2px 4px rgba(0, 0, 0, 0.5);
  transition: all 0.3s;
}

.heartbeat-pill.on {
  color: #69f0ae;
  border-color: rgba(0, 230, 118, 0.35);
  background: rgba(0, 230, 118, 0.08);
}

.heartbeat-pill.on .heartbeat-led {
  background: #00e676;
  box-shadow: 0 0 8px rgba(0, 230, 118, 0.45);
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
  margin: 0 0 12px 0;
  font-size: 14px;
  color: #b0bec5;
}

.auth-tag {
  padding: 6px 10px;
  border-radius: 6px;
  border: 1px solid rgba(255, 152, 0, 0.35);
  background: rgba(255, 152, 0, 0.1);
  color: #ffb74d;
  font-size: 12px;
  font-weight: 600;
  white-space: nowrap;
}

.auth-tag.authorized {
  border-color: rgba(0, 230, 118, 0.35);
  background: rgba(0, 230, 118, 0.08);
  color: #69f0ae;
}

.auth-inline {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 0 0 auto;
}

.btn.auth-combo {
  background: rgba(255, 152, 0, 0.12);
  color: #ffb74d;
  border: 1px dashed rgba(255, 183, 77, 0.45);
  white-space: nowrap;
}

.btn.auth-combo:hover {
  background: rgba(255, 152, 0, 0.2);
}

.cmd-buttons {
  display: flex;
  flex-wrap: nowrap;
  gap: 12px;
  margin-bottom: 24px;
  align-items: center;
  overflow-x: auto;
  padding-bottom: 4px;
}

.cmd-buttons::-webkit-scrollbar {
  height: 6px;
}

.cmd-buttons::-webkit-scrollbar-thumb {
  background: rgba(100, 181, 246, 0.25);
  border-radius: 4px;
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
  flex: 0 0 auto;
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

.pset-input:disabled {
  opacity: 0.35;
  cursor: not-allowed;
}

.btn.cmd {
  background: rgba(142, 36, 170, 0.15);
  color: #ce93d8;
  border: 1px solid rgba(142, 36, 170, 0.3);
  white-space: nowrap;
  flex: 0 0 auto;
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

