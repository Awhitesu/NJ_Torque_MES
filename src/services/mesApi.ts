// MES API 请求服务

import type {
  AppConfig,
  GetOrderRequest,
  GetOrderResponse,
  GetRouteRequest,
  GetRouteResponse,
  CompleteCheckInputRequest
} from '../types/mes'

/**
 * 通用POST请求，支持跨域
 */
async function postRequest<T>(url: string, body: object): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json'
    },
    body: JSON.stringify(body)
  })

  if (!response.ok) {
    throw new Error(`HTTP错误: ${response.status} ${response.statusText}`)
  }

  const data = await response.json()
  return data as T
}

/**
 * 步骤一：通过非首段工序获取工单信息
 * POST /api/0rderInfo/GetOtherOrderInfoByProcess
 */
export async function getOrderByProcess(
  config: AppConfig,
  productCode: string
): Promise<GetOrderResponse> {
  const params: GetOrderRequest = {
    technicsProcessCode: config.technicsProcessCode,
    productCode
  }
  return postRequest<GetOrderResponse>(config.orderApiUrl, params)
}

/**
 * 步骤二：根据routeCode获取工步列表
 * POST /api/0rderInfo/GetTechRouteListByCode
 */
export async function getRouteList(
  config: AppConfig,
  routeCode: string
): Promise<GetRouteResponse> {
  const params: GetRouteRequest = {
    routeCode,
    workSeqNo: config.technicsProcessCode
  }
  return postRequest<GetRouteResponse>(config.routeApiUrl, params)
}

/**
 * 步骤三：全物料校验
 * POST /api/ProduceMessage/CompleteCheckInput
 */
export async function completeCheckInput(
  config: AppConfig,
  data: CompleteCheckInputRequest
): Promise<any> {
  return postRequest<any>(config.fullMaterialApiUrl, data)
}

/**
 * 步骤四：报工数据上传
 * POST /api/ProduceMessage/PushPackMessageToMes
 */
export async function pushPackMessageToMes(
  config: AppConfig,
  data: any[] // 实际是一个数组，按工步分片
): Promise<any> {
  return postRequest<any>(config.mesUploadApiUrl, data)
}


