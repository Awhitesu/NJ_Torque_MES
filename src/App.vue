<script setup lang="ts">
import { ref, reactive, onMounted, onUnmounted, nextTick, computed } from 'vue'
import type { AppConfig, CompleteCheckInputRequest, OrderInfo, RouteStep, TestResult, WorkStep, TighteningTask } from './types/mes'
import { getOrderByProcess, getRouteList, completeCheckInput, pushPackMessageToMes } from './services/mesApi'
import ConfigModal from './components/ConfigModal.vue'
import RouteTable from './components/RouteTable.vue'
import ApiDetail from './components/ApiDetail.vue'
import type { ApiRecord } from './components/ApiDetail.vue'
import MaterialScanner from './components/MaterialScanner.vue'
import TorqueInteraction from './components/TorqueInteraction.vue'
import LoginModal from './components/LoginModal.vue'
import type { User } from './types/mes'
import { clearSignal, writeSignal } from './utils/labviewSignal'
import { pullBarcodeScannerEvents, startBarcodeScanner, stopBarcodeScanner } from './services/barcodeScannerApi'
import { getAppConfig, saveAppConfig } from './services/appConfigApi'

const DEFAULT_CONFIG: AppConfig = {
  orderApiUrl: '/mes-api/api/OrderInfo/GetOtherOrderInfoByProcess',
  routeApiUrl: '/mes-api/api/OrderInfo/GetTechRouteListByCode',
  singleMaterialApiUrl: '/mes-api/api/ProduceMessage/SingleCheckInput',
  fullMaterialApiUrl: '/mes-api/api/ProduceMessage/CompleteCheckInput',
  mesUploadApiUrl: '/mes-push/api/ProduceMessage/PushPackMessageToMes',
  technicsProcessCode: 'CTP_P1240',
  technicsProcessName: '默认工序',
  userName: 'admin',
  userAccount: 'admin',
  deviceCode: '',
  deviceName: '',
  desoutterIp: '192.168.5.212',
  desoutterPort: 4545,
  scannerIp: '',
  scannerPort: 0,
  barcodeRegex: '.*',

  logSavePath: 'C:\\NJ_Torque_Logs',
  tighteningMaxRetries: 3,
  adminUsername: 'admin',
  adminPassword: '123'
}

const config = reactive<AppConfig>({ ...DEFAULT_CONFIG })
const showConfig = ref(false)
const showLogin = ref(false)
const currentUser = ref<User | null>(null)
const loginPurpose = ref<'reset' | 'manual'>('reset')
const manualCommandAuthorized = ref(false)
async function onConfigSaved() {
  try {
    const saved = await saveAppConfig({ ...config })
    Object.assign(config, { ...DEFAULT_CONFIG, ...saved.config })
    addLog('success', `[配置] 已保存到文件: ${saved.filePath}`)
    await restartBarcodeScanner()
  } catch (err: any) {
    addLog('error', `[配置] 保存失败: ${err?.message || err}`)
  }
}

const productCode = ref('')
const scanInputRef = ref<HTMLInputElement | null>(null)
const focusScan = () => nextTick(() => scanInputRef.value?.focus())
onMounted(() => {
  focusScan()
  clearSignal()
  void loadConfigFromFile()
})
onUnmounted(() => {
  stopScannerPolling()
  void stopBarcodeScanner().catch(() => {})
})

const orderInfo = ref<OrderInfo | null>(null)
const orderLoading = ref(false)
const orderError = ref('')
const routeSteps = ref<RouteStep[]>([])
const routeLoading = ref(false)
const routeError = ref('')
const tighteningTasks = ref<TighteningTask[]>([])

const testResult = ref<TestResult>('IDLE')
const resultMessage = ref('')
const logs = ref<any[]>([])
const apiRecords = ref<ApiRecord[]>([])
const activeTab = ref<'route' | 'api' | 'log' | 'material' | 'torque'>('route')

const torqueInteractionRef = ref<any>(null)
const materialScannerRef = ref<any>(null)
const materialVerificationLoading = ref(false)
const materialVerificationSuccess = ref(false)
const tighteningStarted = ref(false)
const verifiedMaterials = ref<any[]>([])
const processStartTime = ref(new Date().toLocaleString())
const scannerRunning = ref(false)
const scannerConnected = ref(false)
const scannerLastCode = ref('')
const scannerLastError = ref('')
const scannerPollError = ref('')
const scannerAfterId = ref(0)
let scannerPollTimer: ReturnType<typeof setInterval> | null = null
const SCANNER_EXCEPTION_LOG_INTERVAL_MS = 60_000
let scannerExceptionLastLogAt = 0
const SCANNER_CONNECTED_STABLE_MS = 2000
const SCANNER_DISCONNECTED_STABLE_MS = 500
let scannerConnectedTrueSince = 0
let scannerConnectedFalseSince = 0

function logScannerException(level: 'warn' | 'error', msg: string) {
  const now = Date.now()
  if (now - scannerExceptionLastLogAt < SCANNER_EXCEPTION_LOG_INTERVAL_MS) return
  scannerExceptionLastLogAt = now
  addLog(level, msg)
}

function setScannerConnectedImmediate(next: boolean) {
  scannerConnected.value = next
  scannerConnectedTrueSince = 0
  scannerConnectedFalseSince = 0
}

function updateScannerConnectedStable(next: boolean) {
  const now = Date.now()

  if (next) {
    scannerConnectedFalseSince = 0
    if (scannerConnected.value) return
    if (scannerConnectedTrueSince === 0) {
      scannerConnectedTrueSince = now
      return
    }
    if (now - scannerConnectedTrueSince >= SCANNER_CONNECTED_STABLE_MS) {
      scannerConnected.value = true
      scannerConnectedTrueSince = 0
    }
    return
  }

  scannerConnectedTrueSince = 0
  if (!scannerConnected.value) {
    scannerConnectedFalseSince = 0
    scannerConnected.value = false
    return
  }
  if (scannerConnectedFalseSince === 0) {
    scannerConnectedFalseSince = now
    return
  }
  if (now - scannerConnectedFalseSince >= SCANNER_DISCONNECTED_STABLE_MS) {
    scannerConnected.value = false
    scannerConnectedFalseSince = 0
  }
}

const globalStatus = computed(() => {
  const msg = resultMessage.value || ''
  const hasPendingTightening = tighteningTasks.value.some(t => t.result === 'PENDING')
  const hasOrderAndRoute = !!orderInfo.value && routeSteps.value.length > 0

  if (testResult.value === 'NG' && resultMessage.value) {
    return { cls: 'fail-mini', icon: 'NG', text: `异常: ${resultMessage.value}` }
  }
  if (msg.includes('报工失败') || msg.includes('报工未完成')) {
    return { cls: 'fail-mini', icon: 'NG', text: `异常: ${msg}` }
  }

  if (orderLoading.value) {
    return { cls: 'loading-mini', icon: '', text: '正在获取工单，请稍候...' }
  }
  if (routeLoading.value) {
    return { cls: 'loading-mini', icon: '', text: '正在获取工步，请稍候...' }
  }
  if (materialVerificationLoading.value) {
    return { cls: 'loading-mini', icon: '', text: '正在向后台提交全物料验证，请稍候...' }
  }

  if (msg.includes('报工已成功') || msg.includes('流程已全部完成') || msg.includes('报工完成')) {
    return { cls: 'success-mini', icon: 'OK', text: '报工完成' }
  }
  if (testResult.value === 'OK' && msg.includes('正在备份日志并报工')) {
    return { cls: 'loading-mini', icon: '', text: '定扭完成，正在报工...' }
  }

  if (materialVerificationSuccess.value && tighteningStarted.value && hasPendingTightening) {
    return { cls: 'loading-mini', icon: '', text: '正在定扭...' }
  }
  if (materialVerificationSuccess.value) {
    return { cls: 'success-mini', icon: 'OK', text: '全物料后台验证已通过，即将进入定扭环节...' }
  }
  if (hasOrderAndRoute) {
    return { cls: 'loading-mini', icon: '', text: '等待单物料扫码' }
  }
  return { cls: 'loading-mini', icon: '', text: '等待扫码...' }
})

async function loadConfigFromFile() {
  try {
    const fileConfig = await getAppConfig()
    Object.assign(config, { ...DEFAULT_CONFIG, ...fileConfig })
    addLog('success', '[配置] 已从 Config 文件加载')
  } catch (err: any) {
    addLog('warn', `[配置] 读取配置文件失败，使用默认配置: ${err?.message || err}`)
  } finally {
    await initBarcodeScanner()
  }
}

function addLog(level: any, msg: string) {
  logs.value.unshift({ time: new Date().toLocaleTimeString(), level, msg })
  if (logs.value.length > 50) logs.value.pop()
}

function isScannerConfigValid() {
  return !!config.scannerIp?.trim() && config.scannerPort > 0
}

async function initBarcodeScanner() {
  if (!isScannerConfigValid()) {
    addLog('warn', '[扫码枪] 未配置扫码枪IP/端口，扫码功能未启动')
    scannerRunning.value = false
    setScannerConnectedImmediate(false)
    return
  }

  try {
    const status = await startBarcodeScanner({
      scannerIp: config.scannerIp.trim(),
      scannerPort: Number(config.scannerPort),
      barcodeRegex: config.barcodeRegex || '.*'
    })

    scannerRunning.value = status.running
    updateScannerConnectedStable(Boolean(status.connected && !status.lastError))
    scannerLastError.value = status.lastError || ''
    addLog('success', `[扫码枪] 模块已启动: ${config.scannerIp}:${config.scannerPort}`)
    startScannerPolling()
  } catch (err: any) {
    scannerRunning.value = false
    setScannerConnectedImmediate(false)
    addLog('error', `[扫码枪] 启动失败: ${err?.message || err}`)
  }
}

async function restartBarcodeScanner() {
  stopScannerPolling()
  scannerAfterId.value = 0
  scannerLastError.value = ''
  scannerLastCode.value = ''
  setScannerConnectedImmediate(false)
  try {
    await stopBarcodeScanner()
  } catch {}
  await initBarcodeScanner()
}

function startScannerPolling() {
  stopScannerPolling()
  scannerPollTimer = setInterval(() => {
    void pollScannerEvents()
  }, 300)
}

function stopScannerPolling() {
  if (!scannerPollTimer) return
  clearInterval(scannerPollTimer)
  scannerPollTimer = null
}

async function pollScannerEvents() {
  if (!scannerRunning.value) return

  try {
    const res = await pullBarcodeScannerEvents(scannerAfterId.value)
    scannerPollError.value = ''
    scannerRunning.value = res.running

    const nextError = (res.lastError || '').trim()
    if (nextError !== scannerLastError.value) {
      scannerLastError.value = nextError
      if (nextError) {
        logScannerException('warn', `[扫码枪] ${nextError}`)
      }
    }
    updateScannerConnectedStable(Boolean(res.running && res.connected && !nextError))

    for (const event of res.events || []) {
      scannerAfterId.value = Math.max(scannerAfterId.value, event.id)
      handleScannerCode(event.code)
    }
  } catch (err: any) {
    const msg = String(err?.message || err || '未知异常')
    if (msg !== scannerPollError.value) {
      scannerPollError.value = msg
      logScannerException('error', `[扫码枪] 轮询失败: ${msg}`)
    }
    setScannerConnectedImmediate(false)
  }
}

function handleScannerCode(rawCode: string) {
  const code = rawCode.trim()
  if (!code) return

  scannerLastCode.value = code
  addLog('info', `[扫码枪] 收到条码: ${code}`)

  if (orderLoading.value || routeLoading.value || materialVerificationLoading.value) {
    addLog('warn', '[扫码枪] 当前流程繁忙，已忽略本次扫码')
    return
  }

  if (!orderInfo.value) {
    productCode.value = code
    void handleScan()
    return
  }

  if (!materialVerificationSuccess.value) {
    materialScannerRef.value?.consumeScannedCode?.(code)
    return
  }

  addLog('warn', '[扫码枪] 当前阶段不接收扫码，请先复位后再扫下一件')
}

function emitLabviewResult(result: TestResult) {
  if (!orderInfo.value) return

  const orderCode = String(orderInfo.value.orderCode || orderInfo.value.order_Code || '')
  const routeNo = String(orderInfo.value.route_No || orderInfo.value.routeNo || '')
  const currentBarcode = productCode.value.trim()

  writeSignal(result, orderCode, currentBarcode, routeNo)
  addLog(result === 'OK' ? 'success' : 'warn', `[LabVIEW] 已写入 ${result} 信号`)
}

function resetAll() {
  orderError.value = ''; routeSteps.value = [];
  routeError.value = ''; testResult.value = 'IDLE'; resultMessage.value = '';
  apiRecords.value = [];
  materialVerificationSuccess.value = false;
  materialVerificationLoading.value = false;
  tighteningStarted.value = false;
}

async function handleScan() {
  const code = productCode.value.trim()
  if (!code || !config.technicsProcessCode) return
  clearSignal()
  resetAll()
  addLog('info', `开始查询工单: ${code}`)
  orderLoading.value = true
  const t0 = Date.now()
  const rec = reactive<ApiRecord>({ title: '获取工单', url: config.orderApiUrl, status: 'pending', time: new Date().toLocaleTimeString(), reqBody: { code } })
  apiRecords.value.unshift(rec)
  activeTab.value = 'api'
  try {
    const res = await getOrderByProcess(config, code)
    rec.duration = Date.now() - t0
    rec.resBody = res
    if (res.datas?.[0]) {
      rec.status = 'success'
      orderInfo.value = res.datas[0]
      addLog('success', '工单获取成功')
      await fetchRouteList(res.datas[0].route_No)
    } else {
      rec.status = 'error'; addLog('error', '未找到工单')
    }
  } catch (err: any) {
    rec.status = 'error'; addLog('error', err.message)
  } finally { orderLoading.value = false }
}

async function fetchRouteList(routeCode: string) {
  routeLoading.value = true
  const t0 = Date.now()
  const rec = reactive<ApiRecord>({ title: '获取工步', url: config.routeApiUrl, status: 'pending', time: new Date().toLocaleTimeString(), reqBody: { routeCode } })
  apiRecords.value.unshift(rec)
  try {
    const res = await getRouteList(config, routeCode)
    rec.duration = Date.now() - t0
    rec.resBody = res
    const steps = (res.data as any)?.workSeqList || (Array.isArray(res.data) ? res.data : [])
    routeSteps.value = steps
    generateTasks(steps)
    rec.status = 'success'; addLog('success', `获取到 ${steps.length} 条工步`)
    activeTab.value = 'material'
  } catch (err: any) {
    rec.status = 'error'; addLog('error', err.message)
  } finally { routeLoading.value = false }
}

function generateTasks(steps: RouteStep[]) {
  const newTasks: TighteningTask[] = []
  const subSteps: WorkStep[] = steps.flatMap(s => s.workStepList || [])
  subSteps.forEach(step => {
    const lineInfo = (step.workStepLineList as any[])?.[0]
    const count = lineInfo?.torqueSettingCount || step.torqueSettingCount || 0
    const pSetNo = String(lineInfo?.pSetNo || step.pSetNo || '--')
    
    if (count > 0) {
      for (let i = 1; i <= count; i++) {
        const task: TighteningTask = {
          id: `${step.workstepNo}-${i}`,
          workstepNo: step.workstepNo || '',
          workstepName: step.workstepName || '',
          pSetNo: pSetNo,
          screwIndex: i,
          itemDisplayName: `螺丝${i}`,
          torqueMin: 0, torqueMax: 0, torqueUnit: '', actualTorque: null,
          angleMin: 0, angleMax: 0, angleUnit: '', actualAngle: null,
          result: 'PENDING',
          retryCount: 0,
          history: []
        };
        (step.workStepParamList || []).forEach(p => {
          if (p.paramName && p.paramName.includes('扭矩')) {
            task.torqueMin = Number(p.minQualityValue) || 0;
            task.torqueMax = Number(p.maxQualityValue) || 0;
            task.torqueUnit = p.paramUnit || '';
          } else if (p.paramName && p.paramName.includes('角度')) {
            task.angleMin = Number(p.minQualityValue) || 0;
            task.angleMax = Number(p.maxQualityValue) || 0;
            task.angleUnit = p.paramUnit || '';
          }
        });
        newTasks.push(task);
      }
    }
  })
  tighteningTasks.value = newTasks
}

function handleSingleMaterialScan(material: { productCode: string, productCount: number }) {
  const rec: ApiRecord = {
    title: '单物料扫描验证',
    url: config.singleMaterialApiUrl || 'LOCAL_MATCH',
    status: 'success',
    time: new Date().toLocaleTimeString(),
    reqBody: material,
    resBody: { message: '匹配成功', code: 200 }
  }
  apiRecords.value.unshift(rec)
}

async function handleMaterialComplete(materials: { productCode: string, productCount: number }[]) {
  if (!orderInfo.value || materialVerificationLoading.value || materialVerificationSuccess.value) {
    console.warn('[调试] 验证正在进行中或已成功，跳过重复触发')
    return
  }
  
  materialVerificationLoading.value = true
  materialVerificationSuccess.value = false
  
  addLog('info', '正在提交全物料验证...')
  const t0 = Date.now()

  const reqData: CompleteCheckInputRequest = {
    produceOrderCode: String(orderInfo.value.orderCode || orderInfo.value.order_Code || ''),
    routeNo: String(orderInfo.value.route_No || orderInfo.value.routeNo || ''),
    technicsProcessCode: config.technicsProcessCode,
    tenantID: 'FD',
    productMixCode: String(orderInfo.value.productMixCode || orderInfo.value.product_MixCode || 'null'),
    productLine: "",
    materialList: materials
  }
  
  console.log('[调试] 全物料验证请求体:', JSON.stringify(reqData, null, 2))

  const rec = reactive<ApiRecord>({ title: '全物料验证', url: config.fullMaterialApiUrl, status: 'pending', time: new Date().toLocaleTimeString(), reqBody: reqData })
  apiRecords.value.unshift(rec)
  
  testResult.value = 'IDLE'
  resultMessage.value = '正在进行全物料后台验证...'
  
  try {
    addLog('info', `[流程] 正在向后台提交真实物料验证 (条码: ${productCode.value})`)


    addLog('info', `[调试] 进入真实 API 验证逻辑 (条码: ${productCode.value})`)
    const res = await completeCheckInput(config, reqData)
    rec.duration = Date.now() - t0
    rec.resBody = res
    
    addLog('info', `[调试] 收到验证回复: ${JSON.stringify(res)}`)
    
    if (res && (res.code === 200 || res.code === "200" || res.success === true || res.msg === '操作成功')) {
      rec.status = 'success'
      addLog('success', '全物料验证通过')
      materialVerificationSuccess.value = true
      verifiedMaterials.value = materials
      testResult.value = 'IDLE'
      resultMessage.value = '物料验证通过，请执行定扭交互'

      
      setTimeout(() => {
        if (materialVerificationSuccess.value) {
          startTighteningWorkflow()
        }
      }, 1500)
    } else {
      rec.status = 'error'
      const msg = res?.message || res?.msg || '未知错误'
      addLog('error', `全物料验证失败: ${msg}`)
      testResult.value = 'NG'
      resultMessage.value = `全物料验证未通过: ${msg}`
      alert(`全物料验证失败！\n原因: ${msg}\n请处理后再继续。`)
    }
  } catch (err: any) {
    rec.status = 'error'
    testResult.value = 'NG'
    resultMessage.value = `请求异常: ${err.message}`
    addLog('error', `全物料验证请求异常: ${err.message}`)
    alert(`全物料验证接口请求失败，请检查网络或配置。\n${err.message}`)
  } finally {
    materialVerificationLoading.value = false
  }
}

function startTighteningWorkflow() {
  addLog('info', '[流程] 正在激活定扭交互组件...')
  tighteningStarted.value = true
  activeTab.value = 'torque'
  if (torqueInteractionRef.value) {
    torqueInteractionRef.value.executeNextPendingTask()
  } else {
    addLog('error', '定扭组件未加载')
  }
}

function handleTaskFailed(failedTask: TighteningTask) {
  const MAX_RETRIES = Math.min(20, Math.max(1, Math.floor(Number(config.tighteningMaxRetries || 3))))
  
  const task = tighteningTasks.value.find(t => t.id === failedTask.id)
  if (!task) return

  task.retryCount = (task.retryCount || 0) + 1
  
  testResult.value = 'NG'
  resultMessage.value = `螺丝拧紧失败: ${task.itemDisplayName} (第${task.retryCount}次尝试)`
  addLog('error', `[流程] ${task.itemDisplayName} 拧紧失败，当前尝试次数: ${task.retryCount}`)

  if (task.retryCount < MAX_RETRIES) {
    alert(`螺丝拧紧失败。\n螺丝: ${task.itemDisplayName}\n当前重试次数: ${task.retryCount} / ${MAX_RETRIES}\n\n请处理现场后点击“确定”重试当前螺丝。`)

    task.result = 'PENDING'
    task.actualTorque = null
    task.actualAngle = null

    testResult.value = 'IDLE'
    resultMessage.value = `已确认重试: ${task.itemDisplayName}，2秒后重新下发工步...`

    addLog('info', `[流程] 失败弹窗确认，准备重试螺丝: ${task.itemDisplayName}`)
    if (torqueInteractionRef.value) {
      torqueInteractionRef.value.resumeTighteningWorkflow()
    }
  } else {
    alert(`定扭失败次数达到上限 (${MAX_RETRIES}次)\n螺丝: ${task.itemDisplayName}\n流程将停止，请手动复位。`)
    addLog('error', `[流程] ${task.itemDisplayName} 达到重试上限，流程停止`)
    if (torqueInteractionRef.value) {
      torqueInteractionRef.value.abortWorkflow()
    }
  }
}

async function handleAllTasksComplete() {
  addLog('success', '所有定扭任务已完成，准备备份并报工...')
  testResult.value = 'OK'
  resultMessage.value = '全部工序已完成，正在备份日志并报工...'
  
  // Submit MES first so the saved file contains the final upload request/response.
  const submitOk = await submitAllDataToMes()
  
  // Save after submission, including both success and failure MES logs.
  await saveAllLogsToLocal()

  emitLabviewResult(submitOk ? 'OK' : 'NG')
}


async function submitAllDataToMes(): Promise<boolean> {
  if (!orderInfo.value) return false

  const t0 = Date.now()
  const nowStr = new Date().toLocaleDateString()
  const endTimeStr = new Date().toLocaleString()
  const currentProductCode = String(productCode.value || '').trim()
  const allTighteningOk = tighteningTasks.value.every(t => t.result === 'PASS')
  const overallProductQuality = allTighteningOk ? 1 : 0
  const produceInEntityList = verifiedMaterials.value
    .filter((m: any) => String(m?.productCode || '').trim() !== currentProductCode)
    .map((m: any) => ({
      productCode: String(m?.productCode || '').trim(),
      ProductCount: 1
    }))
  
  // 1. 鏋勫缓鐗╂枡缁戝畾姝?(STEP1)
  const step1Payload = {
    produceOrderCode: orderInfo.value.orderCode || orderInfo.value.order_Code || '',
    routeNo: orderInfo.value.route_No || orderInfo.value.routeNo || '',
    technicsProcessCode: config.technicsProcessCode,
    technicsProcessName: config.technicsProcessName || '',
      technicsStepCode: "STEP1",
      technicsStepName: "物料绑定",
      productCode: productCode.value,
      productCount: 1,
    productQuality: overallProductQuality,
    produceDate: nowStr,
    startTime: processStartTime.value,
    endTime: endTimeStr,
    userName: config.userName || currentUser.value?.username || "admin",
    userAccount: config.userAccount || currentUser.value?.username || "admin",
    deviceCode: config.deviceCode || "",
    Remarks: "",
    ProduceInEntityList: produceInEntityList,
    produceParamEntityList: [],
    ngEntityList: [],
    cellParamEntityList: [],
    otherParamEntityList: [],
    deviceName: config.deviceName || ""
  }

  // 2. 鎸夊伐姝ュ瀹氭壄浠诲姟杩涜鍒嗙粍 (STEP2, STEP3...)
  const torqueGroups = new Map<string, TighteningTask[]>()
  tighteningTasks.value.forEach(t => {
    const list = torqueGroups.get(t.workstepNo) || []
    list.push(t)
    torqueGroups.set(t.workstepNo, list)
  })

  const torquePayloads = Array.from(torqueGroups.entries()).map(([stepNo, tasks]) => {
    return {
      produceOrderCode: orderInfo.value!.orderCode || orderInfo.value!.order_Code || '',
      routeNo: orderInfo.value!.route_No || orderInfo.value!.routeNo || '',
      technicsProcessCode: config.technicsProcessCode,
      technicsProcessName: config.technicsProcessName || "",
      technicsStepCode: stepNo, // 鍋囪鎺ュ彛浜岃繑鍥炵殑 workseqNo 瀵瑰簲 STEP2, STEP3...
      technicsStepName: tasks[0].workstepName,
      productCode: productCode.value,
      productCount: 1,
      productQuality: tasks.every(t => t.result === 'PASS') ? 1 : 0,
      produceDate: nowStr,
      startTime: processStartTime.value,
      endTime: endTimeStr,
      userName: config.userName || currentUser.value?.username || "admin",
      userAccount: config.userAccount || currentUser.value?.username || "admin",
      deviceCode: config.deviceCode || "",
      Remarks: "",
      ProduceInEntityList: [],
      produceParamEntityList: [],
      ngEntityList: [],
      cellParamEntityList: [],
      otherParamEntityList: tasks.map(t => ({
        productCode: `bolt_${t.screwIndex}`,
        otherInfoList: [
          {
            technicsParamName: "定扭扭矩",
            technicsParamCode: "DN0001",
            technicsParamValue: t.actualTorque || "0",
            desc: "定扭扭矩",
            technicsParamQuality: t.result === 'PASS' ? "1" : "0"
          },
          {
            technicsParamName: "定扭角度",
            technicsParamCode: "DN0002",
            technicsParamValue: t.actualAngle || "0",
            desc: "定扭角度",
            technicsParamQuality: t.result === 'PASS' ? "1" : "0"
          },
          {
            technicsParamName: "程序号",
            technicsParamCode: "DN0005",
            technicsParamValue: t.pSetNo,
            desc: "程序号",
            technicsParamQuality: t.result === 'PASS' ? "1" : "0"
          },
          {
            technicsParamName: "定扭时间",
            technicsParamCode: "DN0007",
            technicsParamValue: t.timestamp || endTimeStr,
            desc: "定扭时间",
            technicsParamQuality: t.result === 'PASS' ? "1" : "0"
          }
        ]
      })),
      deviceName: config.deviceName || ""
    }
  })

  const finalPayload = [step1Payload, ...torquePayloads]
  
  const rec = reactive<ApiRecord>({ 
    title: 'MES 报工上传', 
    url: config.mesUploadApiUrl, 
    status: 'pending', 
    time: new Date().toLocaleTimeString(), 
    reqBody: finalPayload 
  })
  apiRecords.value.unshift(rec)
  addLog('info', `[MES] 开始汇总报工数据 (共${finalPayload.length}个工步)`)

  try {
    const res = await pushPackMessageToMes(config, finalPayload)
    rec.duration = Date.now() - t0
    rec.resBody = res
    if (res && (res.code === 200 || res.success === true)) {
      rec.status = 'success'
      addLog('success', 'MES 报工完成：结果已成功推送到生产服务端')
      resultMessage.value = '报工已成功，当前流程已全部完成。'
      return true
    } else {
      rec.status = 'error'
      const failMsg = res?.message || res?.msg || '服务端拒绝'
      addLog('error', `MES 报工失败: ${failMsg}`)
      resultMessage.value = `报工失败: ${failMsg}`
      return false
    }
  } catch (err: any) {
    rec.status = 'error'
    rec.resBody = err.message
    addLog('error', `MES 报工网络异常: ${err.message}`)
    resultMessage.value = `网络异常，报工未完成: ${err.message}`
    return false
  }
}


async function saveAllLogsToLocal() {
  addLog('info', `[System] 正在发起日志备份请求... (条码: ${productCode.value || '无条码'})`)

  const barcode = productCode.value.trim() || 'NoBarcode'

  const now = new Date()
  const timestamp = now.getFullYear().toString() + 
                    (now.getMonth() + 1).toString().padStart(2, '0') + 
                    now.getDate().toString().padStart(2, '0') + "_" +
                    now.getHours().toString().padStart(2, '0') + 
                    now.getMinutes().toString().padStart(2, '0') + 
                    now.getSeconds().toString().padStart(2, '0')
  
  const fileName = `${barcode}_${timestamp}.txt`
  
  // 组合日志内容
  let content = `================================================\n`
  content += `NJ_Torque_MES 生产执行记录\n`
  content += `产品条码: ${barcode}\n`
  content += `保存时间: ${now.toLocaleString()}\n`
  content += `================================================\n\n`
  
  content += `【操作日志】\n`
  logs.value.slice().reverse().forEach(l => {
    content += `[${l.time}] [${l.level.toUpperCase()}] ${l.msg}\n`
  })
  
  content += `\n【接口交互记录】\n`
  apiRecords.value.slice().reverse().forEach(r => {
    content += `------------------------------------------------\n`
    content += `时间: ${r.time} | 状态: ${r.status.toUpperCase()} | 耗时: ${r.duration || 0}ms\n`
    content += `标题: ${r.title}\n`
    content += `请求: ${JSON.stringify(r.reqBody, null, 2)}\n`
    content += `响应: ${JSON.stringify(r.resBody, null, 2)}\n`
  })
  
  try {
    const response = await fetch('http://127.0.0.1:5246/saveLogs', {


      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        FileName: fileName,
        Content: content,
        Path: config.logSavePath
      })
    })
    
    if (response.ok) {
       const text = await response.text();
       try {
         const resData = JSON.parse(text);
         addLog('success', `[System] 日志已自动备份至本地: ${resData.path}`)
       } catch {
         addLog('success', '[System] 日志备份请求已发送，但后端未返回确认信息')
       }
    } else {
       const text = await response.text();
       addLog('error', `[System] 后台保存失败 (HTTP ${response.status}): ${text.substring(0, 100)}`)
    }
  } catch (err) {
    addLog('error', `[System] 通讯异常: ${err}`)
  }

}





async function resetResult() {
  const isFinished = testResult.value === 'OK' && resultMessage.value.includes('已完成')
  const hasStarted = productCode.value.trim() !== ''

  if (hasStarted && !isFinished) {
    loginPurpose.value = 'reset'
    showLogin.value = true
    return
  }
  await executeReset()
}
async function handleAuthSuccess(user: User) {
  if (loginPurpose.value === 'manual') {
    currentUser.value = user
    manualCommandAuthorized.value = true
    addLog('success', `[权限] 管理员[${user.username}] 已授权快捷下发`)
    return
  }

  currentUser.value = user
  addLog('warn', `[权限] 管理员[${user.username}] 授权：执行强制复位`)
  await executeReset()
  currentUser.value = null
}
async function executeReset() {
  if (productCode.value) {
    addLog('info', '正在后台备份当前流程日志...')
    saveAllLogsToLocal().catch(err => {
      console.error('备份失败:', err)
      addLog('error', '[System] 自动备份过程发生错误')
    })
  }

  clearSignal()
  productCode.value = ''
  orderInfo.value = null
  routeSteps.value = []
  materialVerificationSuccess.value = false
  materialVerificationLoading.value = false
  tighteningStarted.value = false
  manualCommandAuthorized.value = false
  testResult.value = 'IDLE'
  resultMessage.value = ''
  activeTab.value = 'route'

  if (torqueInteractionRef.value) {
    torqueInteractionRef.value.abortWorkflow()
    setTimeout(() => {
      torqueInteractionRef.value?.resetWorkflow()
    }, 100)
  }

  addLog('info', '----------------------------------------')
  addLog('info', '✅ 系统已全面复位，请扫描新工单')
  focusScan()
}

function requestManualCommandAuth() {
  if (manualCommandAuthorized.value) return
  loginPurpose.value = 'manual'
  showLogin.value = true
}
</script>

<template>
  <div class="app-root">
    <header class="app-header">
      <div class="header-left">
        <div class="brand-icon">MES</div>
        <div class="brand-text">
          <span class="brand-title">工序扫码系统</span>
          <span class="brand-sub">MES Process Scanner v1.0</span>
        </div>
      </div>
      <div class="header-center">
        <span class="process-badge">
          <span class="label">当前工序：</span>
          <span class="value">{{ config.technicsProcessCode || '未设置' }}</span>
        </span>
      </div>
      <div class="header-right">
        <button class="icon-btn" title="系统配置" @click="showConfig = true">
          配置
        </button>
      </div>
    </header>

    <main class="app-main">
      <section class="left-panel">

        <div class="card scan-card">
          <div class="card-title">
            <span class="step-badge">1</span>
            扫描产品条码
          </div>
          <div class="scan-status-line">
            <span :class="scannerConnected ? 'status-ok' : 'status-warn'">
              {{ scannerConnected ? '扫码枪已连接' : '扫码枪未连接' }}
            </span>
            <span v-if="scannerLastCode" class="last-scan-code">最近扫码: {{ scannerLastCode }}</span>
          </div>
          <div class="scan-input-wrap" :class="{ 'scanning': orderLoading }">
            <input
              ref="scanInputRef"
              v-model="productCode"
              type="text"
              placeholder="等待扫码枪输入产品条码..."
              class="scan-input"
              :disabled="true"
              readonly
            />
            <button
              class="scan-btn"
              :disabled="true"
            >
              扫码触发
            </button>
          </div>
          <p class="scan-hint">仅允许扫码枪输入，已禁用人工键盘录入</p>
        </div>

        <div class="card info-card">
          <div class="card-title">
            <span class="step-badge">2</span>
            工单信息
            <div v-if="orderLoading" class="loading-spin" />
          </div>

          <div v-if="orderError" class="error-box">
            <span>!</span> {{ orderError }}
          </div>

          <div v-else-if="orderInfo" class="info-grid">
            <div class="info-item">
              <span class="info-label">工单号</span>
              <span class="info-value highlight">{{ orderInfo.orderCode || orderInfo.order_Code || '—' }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">工艺路线编码 (route_No)</span>
              <span class="info-value mono">{{ orderInfo.route_No || orderInfo.routeNo || '—' }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">产品条码</span>
              <span class="info-value mono">{{ productCode }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">工序名称</span>
              <span class="info-value">{{ config.technicsProcessName || config.technicsProcessCode || '—' }}</span>
            </div>
          </div>

          <div v-else class="empty-hint">等待扫码查询...</div>
        </div>

        <div class="card result-card">
          <div class="card-title">
            <span class="step-badge">3</span>
            总体流程状态
          </div>

          <div class="result-display" :class="testResult.toLowerCase()">
            <span class="result-icon">
              {{ testResult === 'OK' ? '✓' : testResult === 'NG' ? '✗' : '…' }}
            </span>
            <span class="result-text">
              {{ testResult === 'IDLE' ? '待执行' : testResult }}
            </span>
            <span v-if="resultMessage" class="result-msg">{{ resultMessage }}</span>
          </div>

          <button
            v-if="productCode.trim() !== ''"
            class="btn-reset"
            @click="resetResult"
          >
            复位状态 / 准备下一件
          </button>


        </div>
      </section>

      <section class="right-panel">
        <div v-if="globalStatus" class="global-status status-banner" :class="globalStatus.cls">
          <span v-if="globalStatus.icon" class="global-status-icon">{{ globalStatus.icon }}</span>
          <span>{{ globalStatus.text }}</span>
        </div>
        <!-- 鏍囩鏍?-->
        <div class="tab-bar">
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'route' }"
            @click="activeTab = 'route'"
          >
            工步列表
            <span v-if="routeSteps.length" class="tab-count">{{ routeSteps.length }}</span>
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'material' }"
            @click="activeTab = 'material'"
          >
            物料验证
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'torque' }"
            @click="activeTab = 'torque'"
          >
            定扭交互
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'api' }"
            @click="activeTab = 'api'"
          >
            接口交互
            <span v-if="apiRecords.length" class="tab-count">{{ apiRecords.length }}</span>
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'log' }"
            @click="activeTab = 'log'"
          >
            操作日志
            <span v-if="logs.length" class="tab-count">{{ logs.length }}</span>
          </button>
        </div>

        <!-- 鏍囩鍐呭鍖?-->
        <div class="tab-content">
          <!-- 宸ユ鍒楄〃 -->
          <div v-show="activeTab === 'route'" class="tab-pane">
            <div v-if="routeError" class="error-box">
              <span>!</span> {{ routeError }}
            </div>
            <RouteTable :steps="routeSteps" :loading="routeLoading" />
          </div>

          <!-- 鐗╂枡楠岃瘉 -->
          <div v-show="activeTab === 'material'" class="tab-pane material-pane">
            <MaterialScanner 
              ref="materialScannerRef"
              :steps="routeSteps"
              :product-code="productCode"
              @log="addLog"
              @single-complete="handleSingleMaterialScan"
              @complete="handleMaterialComplete"
            />
          </div>

          <!-- 瀹氭壄浜や簰 -->
          <div v-show="activeTab === 'torque'" class="tab-pane torque-pane">
            <div class="tightening-matrix-card-modern">
              <div class="matrix-header-modern">
                <span class="matrix-title">定扭判定矩阵 (基于工单工步自动展开)</span>
              </div>
              <div class="matrix-table-wrap">
                <table class="matrix-table-modern">
                  <thead>
                    <tr>
                      <th style="width: 50px">序号</th>
                      <th>工步名称</th>
                      <th>程序号</th>
                      <th>项目名称</th>
                      <th>目标扭矩</th>
                      <th>实测扭矩</th>
                      <th>目标角度</th>
                      <th>实测角度</th>
                      <th>结果</th>
                      <th>次数/历史</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="(task, idx) in tighteningTasks" :key="task.id" :class="task.result">
                      <td>{{ idx + 1 }}</td>
                      <td>{{ task.workstepName }}</td>
                      <td><span class="badge pset">{{ task.pSetNo }}</span></td>
                      <td>{{ task.itemDisplayName }}</td>
                      <td style="color:#78909c; font-size: 11px;">{{ task.torqueMin }} - {{ task.torqueMax }} {{ task.torqueUnit }}</td>
                      <td class="mono b" :style="{ color: task.actualTorque ? '#00e676' : 'inherit' }">{{ task.actualTorque || '--' }}</td>
                      <td style="color:#78909c; font-size: 11px;">{{ task.angleMin }} - {{ task.angleMax }} {{ task.angleUnit }}</td>
                      <td class="mono b" :style="{ color: task.actualAngle ? '#00e676' : 'inherit' }">{{ task.actualAngle || '--' }}</td>
                      <td>
                        <span v-if="task.result === 'PASS'" class="badge pass">OK</span>
                        <span v-else-if="task.result === 'FAIL'" class="badge fail">NG</span>
                        <span v-else class="badge pending">等待</span>
                      </td>
                      <td>
                        <div class="retry-col">
                          <span v-if="task.retryCount > 0" class="retry-count">x{{ task.retryCount }}</span>
                          <span v-else class="retry-zero">--</span>
                          <div v-if="task.history && task.history.length > 1" class="history-preview" :title="task.history.map(h => `${h.timestamp}: ${h.torque}Nm / ${h.angle}Deg [${h.result}]`).join('\n')">
                            历史
                          </div>
                        </div>
                      </td>
                    </tr>
                    <tr v-if="!tighteningTasks.length">
                      <td colspan="9" class="empty-text">暂无定扭配置，请先查询工单工步</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </div>
            <div class="torque-interaction-wrap">
              <TorqueInteraction 
                ref="torqueInteractionRef"
                :ip="config.desoutterIp"
                :port="config.desoutterPort"
                :manual-command-authorized="manualCommandAuthorized"
                v-model:tasks="tighteningTasks"
                @log="addLog"
                @taskFailed="handleTaskFailed"
                @allTasksComplete="handleAllTasksComplete"
                @request-manual-auth="requestManualCommandAuth"
              />
            </div>
          </div>

          <!-- 鎺ュ彛浜や簰璇︽儏 -->
          <div v-show="activeTab === 'api'" class="tab-pane">
            <ApiDetail :records="apiRecords" />
          </div>

          <!-- 鎿嶄綔鏃ュ織 -->
          <div v-show="activeTab === 'log'" class="tab-pane log-pane">
            <div class="log-scroll">
              <div
                v-for="(entry, i) in logs"
                :key="i"
                class="log-entry"
                :class="entry.level"
              >
                <span class="log-time">{{ entry.time }}</span>
                <span class="log-msg">{{ entry.msg }}</span>
              </div>
              <div v-if="!logs.length" class="log-empty">暂无日志</div>
            </div>
          </div>
        </div>
      </section>
    </main>

    <!-- 閰嶇疆寮圭獥 -->
    <ConfigModal
      v-model="config"
      v-model:visible="showConfig"
      @save="onConfigSaved"
    />

    <LoginModal
      v-model:visible="showLogin"
      :admin-user="config.adminUsername"
      :admin-pass="config.adminPassword"
      @auth-success="handleAuthSuccess"
    />

  </div>
</template>


<style scoped>
/* 閺嶇懓顔愰崳?*/
.app-root {
  display: flex;
  flex-direction: column;
  height: 100vh;
  width: 100vw;
  background: #0a0e1a;
  color: #c8d6e5;
  font-family: 'Segoe UI', 'Microsoft YaHei', sans-serif;
  overflow: hidden;
}

/* 妞ゅ爼鍎撮弽鍥暯閺?*/
.app-header {
  display: flex;
  align-items: center;
  padding: 0 20px;
  height: 52px;
  background: linear-gradient(135deg, #0d1b2a 0%, #112240 100%);
  border-bottom: 1px solid rgba(100, 181, 246, 0.2);
  box-shadow: 0 2px 16px rgba(0, 0, 0, 0.4);
  flex-shrink: 0;
  gap: 16px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.brand-icon {
  width: 34px;
  height: 34px;
  background: linear-gradient(135deg, #1565c0, #0d47a1);
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  font-weight: 800;
  color: #e3f2fd;
  letter-spacing: -0.5px;
  box-shadow: 0 0 12px rgba(21, 101, 192, 0.5);
}

.brand-text {
  display: flex;
  flex-direction: column;
}

.brand-title {
  font-size: 15px;
  font-weight: 700;
  color: #e3f2fd;
  line-height: 1.2;
}

.brand-sub {
  font-size: 10px;
  color: #546e7a;
  letter-spacing: 0.5px;
}

.header-center {
  flex: 1;
  display: flex;
  justify-content: center;
}

.process-badge {
  background: rgba(21, 101, 192, 0.2);
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 24px;
  padding: 6px 18px;
  font-size: 13px;
  display: flex;
  gap: 8px;
}

.process-badge .label {
  color: #78909c;
  font-size: 13px;
}

.process-badge .value {
  color: #42a5f5;
  font-weight: 700;
  font-size: 16px;
  line-height: 1;
}

.header-right {
  display: flex;
  gap: 8px;
}

.icon-btn {
  background: rgba(21, 101, 192, 0.2);
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 6px;
  color: #90caf9;
  padding: 5px 14px;
  font-size: 12px;
  cursor: pointer;
  transition: all 0.2s;
}

.icon-btn:hover {
  background: rgba(21, 101, 192, 0.4);
  border-color: #42a5f5;
  color: #e3f2fd;
}

/* 涓讳綋 */
.app-main {
  display: flex;
  gap: 12px;
  padding: 12px;
  flex: 1;
  overflow: hidden;
}

.left-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  width: 360px;
  flex-shrink: 0;
  overflow-y: auto;
}

.right-panel {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  min-width: 0;
  background: #131929;
  border: 1px solid rgba(100, 181, 246, 0.12);
  border-radius: 10px;
}

.global-status {
  margin: 10px 10px 0;
  flex-shrink: 0;
}

/* 閺嶅洨顒烽弽?*/
.tab-bar {
  display: flex;
  gap: 2px;
  padding: 8px 10px 0;
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  background: linear-gradient(180deg, #0d1525 0%, #131929 100%);
  flex-shrink: 0;
}

.tab-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 16px;
  background: transparent;
  border: 1px solid transparent;
  border-bottom: none;
  border-radius: 6px 6px 0 0;
  color: #546e7a;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.2s;
  position: relative;
  bottom: -1px;
}

.tab-btn:hover {
  color: #90caf9;
  background: rgba(100, 181, 246, 0.05);
}
.tab-btn.disabled {
  opacity: 0.5;
  cursor: not-allowed;
  filter: grayscale(0.8);
}
.tab-btn.disabled:hover {
  background: transparent;
  color: inherit;
}


.tab-btn.active {
  color: #42a5f5;
  background: #131929;
  border-color: rgba(100, 181, 246, 0.15);
  font-weight: 600;
}

.tab-count {
  background: rgba(66, 165, 245, 0.2);
  color: #42a5f5;
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 10px;
  font-weight: 600;
}

.tab-btn.active .tab-count {
  background: rgba(66, 165, 245, 0.3);
}

/* 閺嶅洨顒烽崘鍛啇閸?*/
.tab-content {
  flex: 1;
  overflow: hidden;
  position: relative;
}

.tab-pane {
  position: absolute;
  inset: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.material-pane {
  min-height: 0;
}

.torque-pane {
  gap: 10px;
  overflow: auto;
}

.log-pane {
  padding: 10px;
}

/* 闁氨鏁ら崡锛勫 */
.card {
  background: #131929;
  border: 1px solid rgba(100, 181, 246, 0.12);
  border-radius: 10px;
  padding: 14px;
  flex-shrink: 0;
}

.route-card,
.log-card {
  flex-shrink: 1;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.route-card {
  flex: 3;
}

.log-card {
  flex: 2;
}

.card-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  font-weight: 700;
  color: #90caf9;
  margin-bottom: 12px;
  letter-spacing: 0.5px;
}

.step-badge {
  width: 20px;
  height: 20px;
  background: linear-gradient(135deg, #1565c0, #0d47a1);
  border-radius: 50%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  color: white;
  font-weight: 700;
  flex-shrink: 0;
}

.lv-badge {
  margin-left: auto;
  background: rgba(38, 198, 218, 0.1);
  color: #26c6da;
  padding: 2px 8px;
  border-radius: 4px;
  font-size: 10px;
  font-weight: 500;
}

/* 鎵爜杈撳叆 */
.scan-input-wrap {
  display: flex;
  align-items: center;
  gap: 8px;
  background: #0d1117;
  border: 2px solid rgba(100, 181, 246, 0.2);
  border-radius: 8px;
  padding: 4px 6px 4px 12px;
  transition: border-color 0.2s, box-shadow 0.2s;
}

.scan-input-wrap.scanning {
  border-color: #42a5f5;
  box-shadow: 0 0 0 3px rgba(66, 165, 245, 0.1), 0 0 20px rgba(66, 165, 245, 0.2);
}

.scan-input-wrap:focus-within {
  border-color: #42a5f5;
  box-shadow: 0 0 0 3px rgba(66, 165, 245, 0.1);
}

.scan-icon {
  font-size: 16px;
  flex-shrink: 0;
}

.scan-input {
  flex: 1;
  background: none;
  border: none;
  outline: none;
  color: #e0e6ed;
  font-size: 14px;
  font-family: 'Consolas', monospace;
  padding: 8px 0;
  min-width: 0;
}

.scan-input::placeholder {
  color: #37474f;
}

.scan-btn {
  background: linear-gradient(135deg, #1565c0, #0d47a1);
  border: none;
  border-radius: 6px;
  color: #e3f2fd;
  padding: 7px 16px;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.2s;
  white-space: nowrap;
  flex-shrink: 0;
}

.scan-btn:hover:not(:disabled) {
  background: linear-gradient(135deg, #1976d2, #1565c0);
  box-shadow: 0 4px 12px rgba(21, 101, 192, 0.4);
}

.scan-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.scan-hint {
  font-size: 11px;
  color: #37474f;
  margin: 6px 0 0 0;
}

.scan-status-line {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
  gap: 8px;
  font-size: 11px;
}

.status-ok {
  color: #00e676;
}

.status-warn {
  color: #ffab40;
}

.last-scan-code {
  color: #80cbc4;
  font-family: 'Consolas', monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

kbd {
  background: rgba(100, 181, 246, 0.1);
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 3px;
  padding: 1px 5px;
  font-size: 10px;
  color: #64b5f6;
}

/* 瀹搞儱宕熸穱鈩冧紖 */
.info-grid {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.info-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 7px 10px;
  background: rgba(21, 101, 192, 0.06);
  border-radius: 6px;
  border: 1px solid rgba(100, 181, 246, 0.08);
  gap: 8px;
}

.info-label {
  font-size: 11px;
  color: #546e7a;
  flex-shrink: 0;
}

.info-value {
  font-size: 12px;
  color: #cfd8dc;
  text-align: right;
  word-break: break-all;
}

.info-value.highlight {
  color: #42a5f5;
  font-weight: 700;
  font-size: 13px;
}

.info-value.mono {
  font-family: 'Consolas', monospace;
}

.empty-hint {
  text-align: center;
  color: #37474f;
  font-size: 12px;
  padding: 16px 0;
}

/* OK/NG 缁撴灉 */
.result-display {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 16px;
  border-radius: 8px;
  margin-bottom: 12px;
  background: rgba(21, 101, 192, 0.05);
  border: 1px solid rgba(100, 181, 246, 0.1);
  transition: all 0.3s;
}

.result-display.ok {
  background: rgba(0, 230, 118, 0.08);
  border-color: rgba(0, 230, 118, 0.3);
  box-shadow: 0 0 24px rgba(0, 230, 118, 0.15);
}

.result-display.ng {
  background: rgba(255, 82, 82, 0.08);
  border-color: rgba(255, 82, 82, 0.3);
  box-shadow: 0 0 24px rgba(255, 82, 82, 0.15);
}

.result-icon {
  font-size: 28px;
}

.result-text {
  font-size: 28px;
  font-weight: 800;
  letter-spacing: 1px;
  color: #e0e6ed;
}

.result-display.ok .result-text {
  color: #00e676;
}

.result-display.ng .result-text {
  color: #ff5252;
}

.result-msg {
  font-size: 12px;
  color: #78909c;
}

.result-actions {
  display: flex;
  gap: 10px;
  margin-bottom: 10px;
}

.btn-ok,
.btn-ng {
  flex: 1;
  padding: 12px;
  border: none;
  border-radius: 8px;
  font-size: 14px;
  font-weight: 700;
  cursor: pointer;
  transition: all 0.2s;
  letter-spacing: 0.5px;
}

.btn-ok {
  background: linear-gradient(135deg, #00c853, #00897b);
  color: white;
  box-shadow: 0 4px 16px rgba(0, 200, 83, 0.2);
}

.btn-ok:hover:not(:disabled) {
  box-shadow: 0 6px 24px rgba(0, 200, 83, 0.4);
  transform: translateY(-1px);
}

.btn-ng {
  background: linear-gradient(135deg, #f44336, #c62828);
  color: white;
  box-shadow: 0 4px 16px rgba(244, 67, 54, 0.2);
}

.btn-ng:hover:not(:disabled) {
  box-shadow: 0 6px 24px rgba(244, 67, 54, 0.4);
  transform: translateY(-1px);
}

.btn-ok:disabled,
.btn-ng:disabled {
  opacity: 0.3;
  cursor: not-allowed;
  transform: none;
  box-shadow: none;
}

.btn-reset {
  width: 100%;
  padding: 9px;
  background: rgba(100, 181, 246, 0.08);
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 6px;
  color: #90caf9;
  font-size: 12px;
  cursor: pointer;
  transition: all 0.2s;
}

.btn-reset:hover {
  background: rgba(100, 181, 246, 0.15);
  border-color: #42a5f5;
}

/* 闁挎瑨顕ゅ?*/
.error-box {
  background: rgba(244, 67, 54, 0.08);
  border: 1px solid rgba(244, 67, 54, 0.25);
  border-radius: 6px;
  padding: 10px 14px;
  font-size: 12px;
  color: #ef9a9a;
  display: flex;
  gap: 8px;
  align-items: flex-start;
  margin-bottom: 8px;
}

/* 閸旂姾娴囬崝銊ф暰 */
.loading-spin {
  width: 14px;
  height: 14px;
  border: 2px solid rgba(66, 165, 245, 0.2);
  border-top-color: #42a5f5;
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
  margin-left: auto;
}

/* 閺冦儱绻?*/
.log-scroll {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 3px;
  padding-right: 4px;
}

.log-scroll::-webkit-scrollbar {
  width: 4px;
}

.log-scroll::-webkit-scrollbar-thumb {
  background: rgba(100, 181, 246, 0.2);
  border-radius: 2px;
}

.log-entry {
  display: flex;
  gap: 10px;
  font-size: 11px;
  padding: 4px 8px;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.02);
}

.log-entry.success { color: #69f0ae; }
.log-entry.error { color: #ff5252; }
.log-entry.warn { color: #ffab40; }
.log-entry.info { color: #78909c; }

.log-time {
  color: #455a64;
  flex-shrink: 0;
  width: 70px;
}

.log-msg {
  word-break: break-all;
  line-height: 1.5;
}

.log-empty {
  text-align: center;
  color: #37474f;
  font-size: 12px;
  padding: 16px;
}

.status-banner {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  border-radius: 8px;
  font-weight: 600;
  font-size: 14px;
  animation: slideDown 0.3s ease-out;
}

.global-status-icon {
  font-size: 12px;
  font-family: 'Consolas', monospace;
  letter-spacing: 0.5px;
}

.success-mini {
  background: rgba(0, 230, 118, 0.1);
  border: 1px solid rgba(0, 230, 118, 0.2);
  color: #00e676;
}
.fail-mini {
  background: rgba(244, 67, 54, 0.1);
  border: 1px solid rgba(244, 67, 54, 0.2);
  color: #f44336;
}
.loading-mini {
  background: rgba(255, 152, 0, 0.1);
  border: 1px solid rgba(255, 152, 0, 0.2);
  color: #ff9800;
}

@keyframes slideDown {
  from { opacity: 0; transform: translateY(-10px); }
  to { opacity: 1; transform: translateY(0); }
}

/* 瀹氭壄鍒ゅ畾鐭╅樀 */
.tightening-matrix-card-modern {
  margin-top: 0;
  border-top: 1px solid rgba(100, 181, 246, 0.1);
  display: flex;
  flex-direction: column;
  flex: 0 0 auto;
  min-height: 220px;
  max-height: 320px;
  overflow: hidden;
}

.torque-interaction-wrap {
  flex: 1;
  min-height: 420px;
  display: flex;
  width: 100%;
}

.torque-interaction-wrap > * {
  flex: 1;
  min-width: 0;
}

.matrix-header-modern {
  padding: 12px 14px;
  background: rgba(13, 71, 161, 0.15);
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.matrix-title {
  color: #e3f2fd;
  font-size: 13px;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 8px;
}

.matrix-table-wrap {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  scrollbar-width: thin;
  scrollbar-color: rgba(100, 181, 246, 0.2) transparent;
}

.matrix-table-modern {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

.matrix-table-modern th {
  background: #0d1117;
  color: #78909c;
  text-align: left;
  padding: 8px 12px;
  font-weight: 600;
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
  position: sticky;
  top: 0;
  z-index: 10;
}

.matrix-table-modern td {
  padding: 8px 12px;
  border-bottom: 1px solid rgba(100, 181, 246, 0.05);
  color: #cfd8dc;
}

.badge {
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 10px;
  font-weight: 600;
  display: inline-block;
}

.badge.pass { background: rgba(0, 230, 118, 0.15); color: #00e676; }
.badge.fail { background: rgba(244, 67, 54, 0.15); color: #f44336; }
.badge.pending { background: rgba(255, 171, 64, 0.15); color: #ffab40; }

.matrix-mono { font-family: 'Consolas', monospace; color: #64b5f6; }

.btn-reset-light {
  background: #1976d2;
  color: white;
  border: none;
  padding: 4px 10px;
  border-radius: 4px;
  cursor: pointer;
  font-size: 11px;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

.retry-col {
  display: flex;
  align-items: center;
  gap: 8px;
  justify-content: center;
}
.retry-count {
  background: rgba(255, 171, 64, 0.1);
  color: #ffab40;
  padding: 2px 6px;
  border-radius: 10px;
  font-size: 10px;
  font-weight: bold;
}
.retry-zero { color: #546e7a; font-size: 10px; }
.history-preview {
  font-size: 12px;
  cursor: help;
  opacity: 0.8;
}
.history-preview:hover { opacity: 1; }
</style>

