<script setup lang="ts">
import { ref, reactive, onMounted, nextTick } from 'vue'
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

const CONFIG_KEY = 'mes_app_config_v2'
const DEFAULT_CONFIG: AppConfig = {
  orderApiUrl: '/mes-api/api/OrderInfo/GetOtherOrderInfoByProcess',
  routeApiUrl: '/mes-api/api/OrderInfo/GetTechRouteListByCode',
  technicsProcessCode: 'CTP_P1240',
  desoutterIp: '192.168.5.212',
  desoutterPort: 4545,

  logSavePath: 'C:\\NJ_Torque_Logs',
  adminUsername: 'admin',
  adminPassword: '123'
}



function loadConfig(): AppConfig {
  try {
    const raw = localStorage.getItem(CONFIG_KEY)
    if (raw) return { ...DEFAULT_CONFIG, ...JSON.parse(raw) }
  } catch {}
  return { ...DEFAULT_CONFIG }
}

const config = reactive<AppConfig>(loadConfig())
const showConfig = ref(false)
const showLogin = ref(false)
const currentUser = ref<User | null>(null)
const onConfigSaved = () => localStorage.setItem(CONFIG_KEY, JSON.stringify(config))

const productCode = ref('')
const scanInputRef = ref<HTMLInputElement | null>(null)
const focusScan = () => nextTick(() => scanInputRef.value?.focus())
onMounted(focusScan)

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
const materialVerificationLoading = ref(false)
const materialVerificationSuccess = ref(false)
const verifiedMaterials = ref<any[]>([])
const processStartTime = ref(new Date().toLocaleString())

function addLog(level: any, msg: string) {
  logs.value.unshift({ time: new Date().toLocaleTimeString(), level, msg })
  if (logs.value.length > 50) logs.value.pop()
}

function resetAll() {
  orderError.value = ''; routeSteps.value = [];
  routeError.value = ''; testResult.value = 'IDLE'; resultMessage.value = '';
  apiRecords.value = [];
  materialVerificationSuccess.value = false;
  materialVerificationLoading.value = false;
}

async function handleScan() {
  const code = productCode.value.trim()
  if (!code || !config.technicsProcessCode) return
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
    url: 'LOCAL_MATCH',
    status: 'success',
    time: new Date().toLocaleTimeString(),
    reqBody: material,
    resBody: { message: '匹配成功', code: 200 }
  }
  apiRecords.value.unshift(rec)
}

async function handleMaterialComplete(materials: { productCode: string, productCount: number }[]) {
  if (!orderInfo.value || materialVerificationLoading.value || materialVerificationSuccess.value) {
    console.warn('[调试] 验证已在进行中或已成功，跳过重复触发')
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

  const rec = reactive<ApiRecord>({ title: '全物料验证', url: '/mes-api/api/ProduceMessage/CompleteCheckInput', status: 'pending', time: new Date().toLocaleTimeString(), reqBody: reqData })
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
      addLog('success', '✅ 全物料验证通过！')
      materialVerificationSuccess.value = true
      verifiedMaterials.value = materials // 保存已验证的物料清单
      testResult.value = 'IDLE' // 验证通过后状态回归待机，直到定扭开始
      resultMessage.value = '物料验证通过，请执行定扭交互'

      
      setTimeout(() => {
        if (materialVerificationSuccess.value) {
          startTighteningWorkflow()
        }
      }, 1500)
    } else {
      rec.status = 'error'
      const msg = res?.message || res?.msg || '未知错误'
      addLog('error', `❌ 全物料验证失败: ${msg}`)
      testResult.value = 'NG'
      resultMessage.value = `全物料验证未通过: ${msg}`
      alert(`全物料验证失败！\n原因: ${msg}\n请处理后再继续。`)
    }
  } catch (err: any) {
    rec.status = 'error'
    testResult.value = 'NG'
    resultMessage.value = `请求异常: ${err.message}`
    addLog('error', `❌ 全物料验证请求异常: ${err.message}`)
    alert(`全物料验证接口请求失败，请检查网络或配置。\n${err.message}`)
  } finally {
    materialVerificationLoading.value = false
  }
}

function startTighteningWorkflow() {
  addLog('info', '[流程] 🚀 正在激活定扭交互组件...')
  activeTab.value = 'torque'
  if (torqueInteractionRef.value) {
    torqueInteractionRef.value.executeNextPendingTask()
  } else {
    addLog('error', '定扭组件未加载')
  }
}

function handleTaskFailed(failedTask: TighteningTask) {
  const MAX_RETRIES = 3
  
  const task = tighteningTasks.value.find(t => t.id === failedTask.id)
  if (!task) return

  task.retryCount = (task.retryCount || 0) + 1
  
  testResult.value = 'NG'
  resultMessage.value = `螺丝拧紧失败: ${task.itemDisplayName} (第 ${task.retryCount} 次尝试)`
  addLog('error', `[流程] ${task.itemDisplayName} 拧紧失败，当前尝试次数: ${task.retryCount}`)

  if (task.retryCount < MAX_RETRIES) {
    const wantRetry = confirm(`螺丝拧紧失败！\n螺丝: ${task.itemDisplayName}\n当前重试次数: ${task.retryCount} / ${MAX_RETRIES}\n\n点击“确定”：重新执行当前螺丝定扭\n点击“取消”：跳过当前螺丝（标记为失败）并继续下一个`)
    
    if (wantRetry) {
      task.result = 'PENDING'
      task.actualTorque = null
      task.actualAngle = null
      
      testResult.value = 'IDLE'
      resultMessage.value = `正在重试: ${task.itemDisplayName}...`
      
      addLog('info', `[流程] 用户选择重试螺丝: ${task.itemDisplayName}`)
      if (torqueInteractionRef.value) {
        torqueInteractionRef.value.executeNextPendingTask()
      }
    } else {
      addLog('warn', `[流程] 用户选择跳过失败螺丝: ${task.itemDisplayName}`)
      if (torqueInteractionRef.value) {
        torqueInteractionRef.value.resumeTighteningWorkflow()
      }
    }
  } else {
    alert(`定扭失败次数已达上限 (${MAX_RETRIES}次)！\n螺丝: ${task.itemDisplayName}\n流程将停止，请手动复位。`)
    addLog('error', `[流程] ${task.itemDisplayName} 达到重试上限，流程停止。`)
    if (torqueInteractionRef.value) {
      torqueInteractionRef.value.abortWorkflow()
    }
  }
}

async function handleAllTasksComplete() {
  addLog('success', '🎉 所有定扭任务已完成！准备备份并报工...')
  testResult.value = 'OK'
  resultMessage.value = '全部工序已完成，正在备份日志并报工...'
  
  // Submit MES first so the saved file contains the final upload request/response.
  await submitAllDataToMes()
  
  // Save after submission, including both success and failure MES logs.
  await saveAllLogsToLocal()
}


async function submitAllDataToMes() {
  if (!orderInfo.value) return

  const t0 = Date.now()
  const nowStr = new Date().toLocaleDateString()
  const endTimeStr = new Date().toLocaleString()
  
  // 1. 构建物料绑定步 (STEP1)
  const step1Payload = {
    produceOrderCode: orderInfo.value.orderCode || orderInfo.value.order_Code || '',
    routeNo: orderInfo.value.route_No || orderInfo.value.routeNo || '',
    technicsProcessCode: config.technicsProcessCode,
    technicsProcessName: "",
    technicsStepCode: "STEP1",
    technicsStepName: "物料绑定",
    productCode: productCode.value,
    productCount: verifiedMaterials.value.length,
    productQuality: 0,
    produceDate: nowStr,
    startTime: processStartTime.value,
    endTime: endTimeStr,
    userName: currentUser.value?.username || "admin",
    userAccount: currentUser.value?.username || "admin",
    deviceCode: "",
    Remarks: "",
    ProduceInEntityList: verifiedMaterials.value.map(m => ({
      productCode: m.productCode,
      ProductCount: m.productCount
    })),
    produceParamEntityList: [],
    ngEntityList: [],
    cellParamEntityList: [],
    otherParamEntityList: [],
    deviceName: ""
  }

  // 2. 按工步对定扭任务进行分组 (STEP2, STEP3...)
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
      technicsProcessName: "",
      technicsStepCode: stepNo, // 假设接口二返回的 workseqNo 对应 STEP2, STEP3...
      technicsStepName: tasks[0].workstepName,
      productCode: productCode.value,
      productCount: tasks.length,
      productQuality: tasks.every(t => t.result === 'PASS') ? 0 : 1,
      produceDate: nowStr,
      startTime: processStartTime.value,
      endTime: endTimeStr,
      userName: currentUser.value?.username || "admin",
      userAccount: currentUser.value?.username || "admin",
      deviceCode: "",
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
            technicsParamQuality: t.result === 'PASS' ? "0" : "1"
          },
          {
            technicsParamName: "定扭角度",
            technicsParamCode: "DN0002",
            technicsParamValue: t.actualAngle || "0",
            desc: "定扭角度",
            technicsParamQuality: t.result === 'PASS' ? "0" : "1"
          },
          {
            technicsParamName: "程序号",
            technicsParamCode: "DN0005",
            technicsParamValue: t.pSetNo,
            desc: "程序号",
            technicsParamQuality: "0"
          },
          {
            technicsParamName: "定扭时间",
            technicsParamCode: "DN0007",
            technicsParamValue: t.timestamp || endTimeStr,
            desc: "定扭时间",
            technicsParamQuality: "0"
          }
        ]
      })),
      deviceName: ""
    }
  })

  const finalPayload = [step1Payload, ...torquePayloads]
  
  const rec = reactive<ApiRecord>({ 
    title: 'MES 报工上传', 
    url: '/mes-push/api/ProduceMessage/PushPackMessageToMes', 
    status: 'pending', 
    time: new Date().toLocaleTimeString(), 
    reqBody: finalPayload 
  })
  apiRecords.value.unshift(rec)
  addLog('info', `[MES] 开始汇总报工数据 (共 ${finalPayload.length} 个工步)`)

  try {
    const res = await pushPackMessageToMes(config, finalPayload)
    rec.duration = Date.now() - t0
    rec.resBody = res
    if (res && (res.code === 200 || res.success === true)) {
      rec.status = 'success'
      addLog('success', '✅ MES 报工完成: 结果已成功推送到生产服务器')
      resultMessage.value = '报工已成功，当前流程已全部完成。'
    } else {
      rec.status = 'error'
      const failMsg = res?.message || res?.msg || '服务器拒绝'
      addLog('error', `❌ MES 报工失败: ${failMsg}`)
      resultMessage.value = `报工失败: ${failMsg}`
    }
  } catch (err: any) {
    rec.status = 'error'
    rec.resBody = err.message
    addLog('error', `❌ MES 报工网络异常: ${err.message}`)
    resultMessage.value = `网络异常，报工未完成: ${err.message}`
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
         addLog('success', '[System] 日志备份请求已发送，但后台未返回确认信息')
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
  // 核心判定：只要输入了条码，且没有达到最终的“全部完成”状态，就需要管理员授权
  const isFinished = testResult.value === 'OK' && resultMessage.value.includes('已完成')
  const hasStarted = productCode.value.trim() !== ''

  if (hasStarted && !isFinished) {
     showLogin.value = true
     return
  }
  await executeReset()
}


async function handleAuthSuccess(user: User) {
  currentUser.value = user
  addLog('warn', `管理员 [${user.username}] 授权：执行强制复位`)
  await executeReset()
  currentUser.value = null // 授权完重置身份
}

async function executeReset() {
  // 1. 尝试保存日志（不阻塞 UI 复位）
  if (productCode.value) {
    addLog('info', '正在后台备份当前流程日志...')
    saveAllLogsToLocal().catch(err => {
      console.error('备份失败:', err)
      addLog('error', '[System] 自动备份过程发生错误')
    })
  }

  // 2. 彻底清理前端状态，回到初始扫码状态
  productCode.value = ''
  orderInfo.value = null
  routeSteps.value = []
  materialVerificationSuccess.value = false
  testResult.value = 'IDLE'
  resultMessage.value = ''
  activeTab.value = 'route' // 回到第一步标签页
  
  if (torqueInteractionRef.value) {
    torqueInteractionRef.value.abortWorkflow()
    setTimeout(() => {
      torqueInteractionRef.value?.resetWorkflow()
    }, 100)
  }

  addLog('info', '----------------------------------------')
  addLog('info', '✅ 系统已全面复位，请扫描新工单')
  
  // 重新聚焦扫码框
  focusScan()
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
          ⚙️ 配置
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
          <div class="scan-input-wrap" :class="{ 'scanning': orderLoading }">
            <span class="scan-icon">📷</span>
            <input
              ref="scanInputRef"
              v-model="productCode"
              type="text"
              placeholder="请扫描或输入产品条码..."
              class="scan-input"
              :disabled="orderLoading || routeLoading"
              @keydown.enter="handleScan"
            />
            <button
              class="scan-btn"
              :disabled="orderLoading || !productCode.trim()"
              @click="handleScan"
            >
              {{ orderLoading ? '查询中...' : '查询' }}
            </button>
          </div>
          <p class="scan-hint">扫描后请按 <kbd>Enter</kbd> 提交</p>
        </div>

        <div class="card info-card">
          <div class="card-title">
            <span class="step-badge">2</span>
            工单信息
            <div v-if="orderLoading" class="loading-spin" />
          </div>

          <div v-if="orderError" class="error-box">
            <span>⚠️</span> {{ orderError }}
          </div>

          <div v-else-if="orderInfo" class="info-grid">
            <div class="info-item">
              <span class="info-label">工单号</span>
              <span class="info-value highlight">{{ orderInfo.orderCode }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">工艺路线编码 (route_No)</span>
              <span class="info-value mono">{{ orderInfo.route_No }}</span>
            </div>
            <div class="info-item">
              <span class="info-label">产品条码</span>
              <span class="info-value mono">{{ productCode }}</span>
            </div>
            <template v-for="(val, key) in orderInfo" :key="key">
              <div
                v-if="key !== 'orderCode' && key !== 'route_No'"
                class="info-item"
              >
                <span class="info-label">{{ key }}</span>
                <span class="info-value">{{ val }}</span>
              </div>
            </template>
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
              {{ testResult === 'OK' ? '✅' : testResult === 'NG' ? '❌' : '⏳' }}
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
            🔄 复位状态 / 准备下一件
          </button>


        </div>
      </section>

      <section class="right-panel">
        <!-- 标签栏 -->
        <div class="tab-bar">
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'route' }"
            @click="activeTab = 'route'"
          >
            <span>📋</span> 工步列表
            <span v-if="routeSteps.length" class="tab-count">{{ routeSteps.length }}</span>
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'material' }"
            @click="activeTab = 'material'"
          >
            <span>📦</span> 物料验证
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'torque' }"
            @click="activeTab = 'torque'"
          >
            <span>🔧</span> 定扭交互
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'api' }"
            @click="activeTab = 'api'"
          >
            <span>🔌</span> 接口交互
            <span v-if="apiRecords.length" class="tab-count">{{ apiRecords.length }}</span>
          </button>
          <button
            class="tab-btn"
            :class="{ active: activeTab === 'log' }"
            @click="activeTab = 'log'"
          >
            <span>📄</span> 操作日志
            <span v-if="logs.length" class="tab-count">{{ logs.length }}</span>
          </button>
        </div>

        <!-- 标签内容区 -->
        <div class="tab-content">
          <!-- 工步列表 -->
          <div v-show="activeTab === 'route'" class="tab-pane">
            <div v-if="routeError" class="error-box">
              <span>⚠️</span> {{ routeError }}
            </div>
            <RouteTable :steps="routeSteps" :loading="routeLoading" />
          </div>

          <!-- 物料验证 -->
          <div v-show="activeTab === 'material'" class="tab-pane flex-column">
            <div v-if="materialVerificationLoading" class="status-banner loading-mini">
              <span class="spinner-icon">⏳</span>
              <span>正在向后台提交全物料验证，请稍候...</span>
            </div>
            <div v-if="materialVerificationSuccess && activeTab === 'material'" class="status-banner success-mini">
              <span class="pulse-icon">✅</span>
              <span>全物料后台验证已通过，即将进入定扭环节...</span>
            </div>
            <div v-if="testResult === 'NG' && activeTab === 'material'" class="status-banner fail-mini">
              <span class="pulse-icon">❌</span>
              <span>物料验证异常: {{ resultMessage }}</span>
            </div>
            <MaterialScanner 
              :steps="routeSteps" 
              @log="addLog"
              @single-complete="handleSingleMaterialScan"
              @complete="handleMaterialComplete"
            />


            <!-- 定扭判定矩阵表格 (已按需移动至物料下方) -->
            <div class="tightening-matrix-card-modern">
              <div class="matrix-header-modern">
                <span class="matrix-title">🔥 定扭判定矩阵 (基于工单工步自动展解)</span>
                <button class="btn-text-modern" @click="tighteningTasks.forEach(t => { t.actualTorque = null; t.actualAngle = null; t.result = 'PENDING'; })">重置进度</button>
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
                            📜 历史
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
          </div>

          <!-- 定扭交互 -->
          <div v-show="activeTab === 'torque'" class="tab-pane">
            <TorqueInteraction 
              ref="torqueInteractionRef"
              :ip="config.desoutterIp"
              :port="config.desoutterPort"
              v-model:tasks="tighteningTasks"
              @log="addLog"
              @taskFailed="handleTaskFailed"
              @allTasksComplete="handleAllTasksComplete"
            />
          </div>

          <!-- 接口交互详情 -->
          <div v-show="activeTab === 'api'" class="tab-pane">
            <ApiDetail :records="apiRecords" />
          </div>

          <!-- 操作日志 -->
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

    <!-- 配置弹窗 -->
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
/* 鏍瑰鍣?*/
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

/* 椤堕儴鏍囬鏍?*/
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
  border-radius: 20px;
  padding: 4px 16px;
  font-size: 12px;
  display: flex;
  gap: 6px;
}

.process-badge .label {
  color: #78909c;
}

.process-badge .value {
  color: #42a5f5;
  font-weight: 600;
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

/* 主体 */
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

/* 鏍囩鏍?*/
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

/* 鏍囩鍐呭鍖?*/
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
}

.log-pane {
  padding: 10px;
}

/* 閫氱敤鍗＄墖 */
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

/* 扫码输入 */
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

kbd {
  background: rgba(100, 181, 246, 0.1);
  border: 1px solid rgba(100, 181, 246, 0.2);
  border-radius: 3px;
  padding: 1px 5px;
  font-size: 10px;
  color: #64b5f6;
}

/* 宸ュ崟淇℃伅 */
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

/* OK/NG 结果 */
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

/* 閿欒妗?*/
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

/* 鍔犺浇鍔ㄧ敾 */
.loading-spin {
  width: 14px;
  height: 14px;
  border: 2px solid rgba(66, 165, 245, 0.2);
  border-top-color: #42a5f5;
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
  margin-left: auto;
}

/* 鏃ュ織 */
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
  margin-bottom: 16px;
  font-weight: 600;
  font-size: 14px;
  animation: slideDown 0.3s ease-out;
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
.pulse-icon {
  font-size: 18px;
  animation: pulse 2s infinite;
}
.spinner-icon {
  font-size: 18px;
  display: inline-block;
  animation: rotate 2s linear infinite;
}

@keyframes rotate {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}


@keyframes slideDown {
  from { opacity: 0; transform: translateY(-10px); }
  to { opacity: 1; transform: translateY(0); }
}
@keyframes pulse {
  0% { transform: scale(1); opacity: 1; }
  50% { transform: scale(1.1); opacity: 0.8; }
  100% { transform: scale(1); opacity: 1; }
}


/* 定扭判定矩阵 (瀵归綈物料验证 UI) */
.tightening-matrix-card-modern {
  margin-top: 24px;
  border-top: 1px solid rgba(100, 181, 246, 0.1);
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

.matrix-table-modern {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}

.matrix-table-modern th {
  background: rgba(21, 101, 192, 0.2);
  color: #78909c;
  text-align: left;
  padding: 8px 12px;
  font-weight: 600;
  border-bottom: 1px solid rgba(100, 181, 246, 0.1);
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


/* 定扭判定矩阵 (对齐物料验证 UI) */
.tightening-matrix-card-modern {
  margin-top: 24px;
  border-top: 1px solid rgba(100, 181, 246, 0.1);
  display: flex;
  flex-direction: column;
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
  max-height: 500px;
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
</style>
