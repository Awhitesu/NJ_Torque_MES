<script setup lang="ts">
import { ref, reactive, onMounted, nextTick } from 'vue'
import type { AppConfig, OrderInfo, RouteStep, TestResult, WorkStep, TighteningTask } from './types/mes'
import { getOrderByProcess, getRouteList, completeCheckInput } from './services/mesApi'
import ConfigModal from './components/ConfigModal.vue'
import RouteTable from './components/RouteTable.vue'
import ApiDetail from './components/ApiDetail.vue'
import type { ApiRecord } from './components/ApiDetail.vue'
import MaterialScanner from './components/MaterialScanner.vue'
import TorqueInteraction from './components/TorqueInteraction.vue'
import SimulatorControl from './components/SimulatorControl.vue'
import { MOCK_ORDER_INFO, MOCK_ROUTE_DATA } from './utils/mockData'

const CONFIG_KEY = 'mes_app_config_v2'
const DEFAULT_CONFIG: AppConfig = {
  orderApiUrl: '/mes-api/api/OrderInfo/GetOtherOrderInfoByProcess',
  routeApiUrl: '/mes-api/api/OrderInfo/GetTechRouteListByCode',
  technicsProcessCode: 'CTP_P1240',
  desoutterIp: '192.168.5.212',
  desoutterPort: 4545
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

function addLog(level: any, msg: string) {
  logs.value.unshift({ time: new Date().toLocaleTimeString(), level, msg })
  if (logs.value.length > 50) logs.value.pop()
}

function resetAll() {
  orderInfo.value = null; orderError.value = ''; routeSteps.value = [];
  routeError.value = ''; testResult.value = 'IDLE'; resultMessage.value = '';
  apiRecords.value = [];
}

async function handleScan() {
  const code = productCode.value.trim()
  if (!code || !config.technicsProcessCode) return
  resetAll()
  addLog('info', `开始查询工单: ${code}`)
  orderLoading.value = true
  const t0 = Date.now()
  const rec: ApiRecord = { title: '获取工单', url: config.orderApiUrl, status: 'pending', time: new Date().toLocaleTimeString(), reqBody: { code } }
  apiRecords.value.unshift(rec)
  activeTab.value = 'api'
  try {
    // 【仿真逻辑】：如果以 MOCK- 开头，直接使用模拟数据
    if (code.toUpperCase().startsWith('MOCK-')) {
      await new Promise(r => setTimeout(r, 800)) // 模拟网络延迟
      rec.duration = Date.now() - t0
      rec.resBody = { code: 200, datas: [MOCK_ORDER_INFO] }
      rec.status = 'success'
      orderInfo.value = MOCK_ORDER_INFO
      addLog('success', '[仿真] 模拟工单获取成功')
      await fetchRouteList(MOCK_ORDER_INFO.route_No)
      return
    }

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
  const rec: ApiRecord = { title: '获取工步', url: config.routeApiUrl, status: 'pending', time: new Date().toLocaleTimeString(), reqBody: { routeCode } }
  apiRecords.value.unshift(rec)
  try {
    // 【仿真逻辑】：只有当产品条码是 MOCK- 开头时，才使用模拟数据
    if (productCode.value.toUpperCase().startsWith('MOCK-')) {
       await new Promise(r => setTimeout(r, 500))
       rec.duration = Date.now() - t0
       rec.resBody = MOCK_ROUTE_DATA
       const steps = MOCK_ROUTE_DATA.data.workSeqList
       routeSteps.value = steps
       generateTasks(steps)
       rec.status = 'success'; addLog('success', '[仿真] 模拟工艺路线获取成功')
       activeTab.value = 'material'
       return
    }

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

// 步骤三：全物料验证
async function handleMaterialComplete(materials: { productCode: string, productCount: number }[]) {
  if (!orderInfo.value) return
  
  addLog('info', '正在提交全物料验证...')
  const t0 = Date.now()
  const reqData = {
    produceOrderCode: orderInfo.value.orderCode,
    routeNo: orderInfo.value.route_No,
    technicsProcessCode: config.technicsProcessCode,
    tenantID: 'FD',
    productMixCode: orderInfo.value.productMixCode || 'null',
    productLine: orderInfo.value.productline_no || '',
    materialList: materials
  }
  
  const rec: ApiRecord = { title: '全物料验证', url: `${config.orderApiUrl}/../ProduceMessage/CompleteCheckInput`, status: 'pending', time: new Date().toLocaleTimeString(), reqBody: reqData }
  apiRecords.value.unshift(rec)
  
  try {
    // 仿真拦截
    if (productCode.value.toUpperCase().startsWith('MOCK-')) {
      await new Promise(r => setTimeout(r, 600))
      rec.status = 'success'
      addLog('success', '[仿真] 全物料验证通过！准备开始定扭...')
      startTighteningWorkflow()
      return
    }

    const res = await completeCheckInput(config, reqData)
    rec.duration = Date.now() - t0
    rec.resBody = res
    // 假设非抛错即为成功，或者检查 res.code
    rec.status = 'success'
    addLog('success', '全物料验证通过！准备开始定扭...')
    testResult.value = 'OK'
    resultMessage.value = '物料绑定全部成功，准备定扭'
    startTighteningWorkflow()
  } catch (err: any) {
    rec.status = 'error'
    testResult.value = 'NG'
    resultMessage.value = '物料验证失败'
    addLog('error', `物料验证失败: ${err.message}`)
    alert(`全物料验证失败，请检查。\n错误信息: ${err.message}`)
  }
}

// 开始自动定扭流程
function startTighteningWorkflow() {
  activeTab.value = 'torque'
  if (torqueInteractionRef.value) {
    torqueInteractionRef.value.executeNextPendingTask()
  } else {
    addLog('error', '定扭组件未加载')
  }
}

// 定扭单步失败处理
function handleTaskFailed(failedTask: TighteningTask) {
  const MAX_RETRIES = 3
  
  // 找到真正的响应式任务对象
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
      
      // 清除大标题的 NG 状态
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

// 所有定扭完成
function handleAllTasksComplete() {
  testResult.value = 'OK'
  resultMessage.value = '全部工步执行完毕，等待数据上传'
  addLog('success', '🎉 当前产品所有定扭任务已完成！等待结果上传逻辑...')
  // TODO: 上传结果
}

// 处理仿真产生的定扭结果
function handleMockTorque(data: { torque: string, angle: string, status: string }) {
  const updatedTasks = JSON.parse(JSON.stringify(tighteningTasks.value)) as TighteningTask[]
  const nextIdx = updatedTasks.findIndex(t => t.result === 'PENDING')
  
  if (nextIdx !== -1) {
    const task = updatedTasks[nextIdx]
    const now = new Date()
    const timestamp = `${now.toLocaleDateString()} ${now.toLocaleTimeString()}`
    const isPass = data.status === 'OK'

    // 保存到历史
    task.history.push({
      torque: data.torque,
      angle: data.angle,
      result: isPass ? 'PASS' : 'FAIL',
      timestamp: timestamp
    })

    task.actualTorque = data.torque
    task.actualAngle = data.angle
    task.timestamp = timestamp
    task.result = isPass ? 'PASS' : 'FAIL'
    
    tighteningTasks.value = updatedTasks
    addLog('success', `[仿真结果] ${task.itemDisplayName}: ${data.torque}Nm / ${data.angle}Deg [${data.status}]`)

    if (task.result === 'FAIL') {
      handleTaskFailed(task)
    } else {
      addLog('info', `[仿真流程] 拧紧成功，即将执行下一个...`)
      setTimeout(() => {
        if (torqueInteractionRef.value) {
          torqueInteractionRef.value.executeNextPendingTask()
        }
      }, 500)
    }
  }
}

function resetResult() {
  testResult.value = 'IDLE'
  resultMessage.value = ''
  // 中止定扭流程中的等待循环（如果正在等待连接）
  if (torqueInteractionRef.value) {
    torqueInteractionRef.value.abortWorkflow()
    // 稍后清除中止标志，以便下次流程可以正常启动
    setTimeout(() => torqueInteractionRef.value?.resetWorkflow(), 100)
  }
  addLog('info', '状态已复位，等待下一件')
}
</script>

<template>
  <div class="app-root">
    <!-- 顶部标题栏 -->
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

    <!-- 主体内容 -->
    <main class="app-main">
      <!-- 左边：操作区 -->
      <section class="left-panel">

        <!-- 扫码输入区 -->
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

        <!-- 工单信息区 -->
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
            <!-- 显示其他返回字段 -->
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

        <!-- 状态及进度展示区域 -->
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
            v-if="testResult !== 'IDLE'"
            class="btn-reset"
            @click="resetResult"
          >
            🔄 复位状态 / 准备下一件
          </button>
        </div>

        <!-- 仿真工具区 -->
        <SimulatorControl 
          v-model:productCode="productCode"
          :tasks="tighteningTasks"
          @log="addLog"
          @mockTorque="handleMockTorque"
        />
      </section>

      <!-- 右边：标签页数据区 -->
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
            <MaterialScanner 
              :steps="routeSteps" 
              @log="addLog"
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
  max-height: 350px;
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